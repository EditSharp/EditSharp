using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EditSharp;

namespace EditSharp.Render
{
    /// <summary>
    /// Computes a full-file SHA-256 checksum of a source's contents — the
    /// key OptimizedMediaCache uses to find a source's optimized media
    /// regardless of where the file currently lives on disk (see
    /// OptimizedMediaCache's own remarks for why content, not path, is the
    /// cache key: a moved or duplicated source still resolves to the same
    /// cache entry).
    ///
    /// FULL-FILE HASH, DECIDED IN CONVERSATION: a partial/sampled hash
    /// (file size + a few sampled chunks) would resolve near-instantly even
    /// on multi-gigabyte footage, at the cost of a vanishingly small but
    /// nonzero chance two genuinely different files collide. Full-file
    /// SHA-256 was chosen instead, deliberately trading that startup-
    /// latency cost for an unconditional correctness guarantee — two
    /// different files never share a cache entry. On a multi-gigabyte
    /// source this means genuinely paying to read the whole file once
    /// before a cache hit/miss is even known; MEMOIZATION below is what
    /// keeps that a once-per-file-per-content cost rather than a
    /// once-per-call one.
    ///
    /// MEMOIZED TWO WAYS:
    ///   1. IN-PROCESS (_inMemory): keyed on the file's own (Length,
    ///      LastWriteTimeUtc) — if neither has changed since this process
    ///      last hashed this exact path, the cached digest is reused with
    ///      no I/O at all. Covers the common case of the same session
    ///      touching the same source many times (probe, decode-plan check,
    ///      playback start, a later seek's session restart).
    ///   2. ON-DISK (a small sidecar index under EditSharpConfig.
    ///      OptimizedMediaDirectory): the in-process cache is gone the
    ///      moment the process exits, which would defeat the whole
    ///      "persistence between app runs" point of an NLE reopening the
    ///      same project — every source would pay a full re-hash on every
    ///      app launch otherwise. The sidecar remembers (path, length,
    ///      lastWriteTimeUtc) -> hash across runs; a file whose size/mtime
    ///      still match what's recorded skips hashing entirely, and any
    ///      mismatch (the file changed, or a genuinely different file now
    ///      sits at that path) falls through to a real re-hash rather than
    ///      trusting stale data.
    ///
    /// NOT a security-sensitive hash — collision resistance here is about
    /// accidentally reusing a different file's optimized media, not
    /// defending against a deliberate adversary, so SHA-256 is chosen for
    /// being fast, built into .NET with no extra dependency, and
    /// effectively collision-free for this purpose rather than for any
    /// cryptographic property specifically.
    /// </summary>
    internal static class MediaHasher
    {
        private readonly record struct CacheKey(string Path, long Length, long LastWriteTimeUtcTicks);

        private sealed class SidecarEntry
        {
            public long Length { get; set; }
            public long LastWriteTimeUtcTicks { get; set; }
            public string Hash { get; set; } = "";
        }

        private static readonly ConcurrentDictionary<CacheKey, string> _inMemory = new();

        // Guards the on-disk sidecar index against concurrent read/write from
        // multiple hash requests in the same process. Cross-PROCESS races on
        // the same index file are resolved by "last writer wins" on save,
        // which loses at most one process's newly-learned entries — never
        // corrupts the file itself, since the whole file is rewritten
        // atomically via a temp file + File.Move, never edited in place.
        private static readonly SemaphoreSlim _sidecarGate = new(1, 1);
        private static Dictionary<string, SidecarEntry>? _sidecar;

        /// <summary>
        /// Lower-case hex SHA-256 of `path`'s current contents. Throws
        /// FileNotFoundException if the file doesn't exist — callers that
        /// need "missing file" to be a soft failure should check
        /// File.Exists themselves first (see OptimizedMediaCache's own
        /// try/catch around its cache lookups for why a hashing failure
        /// must never take playback or render down with it).
        /// </summary>
        public static async Task<string> ComputeAsync(string path, CancellationToken ct = default)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                throw new FileNotFoundException($"Cannot hash '{path}' — file not found.", path);

            var key = new CacheKey(NormalizePath(path), info.Length, info.LastWriteTimeUtc.Ticks);

            if (_inMemory.TryGetValue(key, out string? cached))
                return cached;

            string? fromSidecar = await TryReadSidecarAsync(key, ct);
            if (fromSidecar != null)
            {
                _inMemory[key] = fromSidecar;
                return fromSidecar;
            }

            string hash = await HashFileAsync(path, ct);

            _inMemory[key] = hash;
            await WriteSidecarAsync(key, hash, ct);

            return hash;
        }

        private static async Task<string> HashFileAsync(string path, CancellationToken ct)
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1 << 20, useAsync: true);

            // Static SHA256.HashDataAsync (.NET 6+) rather than
            // SHA256.Create()+ComputeHash — no intermediate HashAlgorithm
            // instance to dispose, and it streams the file rather than
            // requiring it all in memory at once, which matters here since
            // sources can legitimately be many gigabytes.
            byte[] hashBytes = await SHA256.HashDataAsync(stream, ct);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        private static string NormalizePath(string path) =>
            // Windows paths are case-insensitive, and this project's own
            // GpuContext remarks call out the actual target machine as
            // Windows-first — normalizing case here means "C:\Foo.mp4" and
            // "c:\foo.mp4" hash-memoize as the same file rather than
            // silently missing the in-process/sidecar cache for a path that
            // only differs in case.
            Path.GetFullPath(path).ToLowerInvariant();

        private static string SidecarPath =>
            Path.Combine(EditSharpConfig.OptimizedMediaDirectory, "hash-index.json");

        private static async Task EnsureSidecarLoadedAsync(CancellationToken ct)
        {
            if (_sidecar != null) return;

            if (File.Exists(SidecarPath))
            {
                try
                {
                    string json = await File.ReadAllTextAsync(SidecarPath, ct);
                    _sidecar = JsonSerializer.Deserialize<Dictionary<string, SidecarEntry>>(json)
                        ?? new Dictionary<string, SidecarEntry>();
                    return;
                }
                catch (Exception ex)
                {
                    // Corrupt/unreadable sidecar — treat as empty rather
                    // than throwing. Every hash just gets recomputed once,
                    // and the file heals itself on the next successful
                    // write below.
                    EditSharpConfig.Logger.LogVerbose(
                        $"MediaHasher: hash sidecar unreadable, rebuilding: {ex.Message}");
                }
            }

            _sidecar = new Dictionary<string, SidecarEntry>();
        }

        private static async Task<string?> TryReadSidecarAsync(CacheKey key, CancellationToken ct)
        {
            await _sidecarGate.WaitAsync(ct);
            try
            {
                await EnsureSidecarLoadedAsync(ct);

                if (_sidecar!.TryGetValue(key.Path, out SidecarEntry? entry) &&
                    entry.Length == key.Length &&
                    entry.LastWriteTimeUtcTicks == key.LastWriteTimeUtcTicks)
                {
                    return entry.Hash;
                }

                return null;
            }
            finally
            {
                _sidecarGate.Release();
            }
        }

        private static async Task WriteSidecarAsync(CacheKey key, string hash, CancellationToken ct)
        {
            await _sidecarGate.WaitAsync(ct);
            try
            {
                await EnsureSidecarLoadedAsync(ct);

                _sidecar![key.Path] = new SidecarEntry
                {
                    Length = key.Length,
                    LastWriteTimeUtcTicks = key.LastWriteTimeUtcTicks,
                    Hash = hash,
                };

                Directory.CreateDirectory(EditSharpConfig.OptimizedMediaDirectory);
                string tempPath = SidecarPath + $".tmp-{Guid.NewGuid():N}";

                await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(_sidecar), ct);

                // Atomic on the same volume — never leaves the sidecar file
                // itself half-written for a concurrent reader to trip over.
                File.Move(tempPath, SidecarPath, overwrite: true);
            }
            catch (Exception ex)
            {
                // The sidecar is a pure performance optimization (skip a
                // re-hash on the next run) — never load-bearing for
                // correctness. A write failure here (read-only disk, full
                // disk, permissions) must never take down whatever asked
                // for a hash in the first place; it just means this file
                // pays a full re-hash again next run.
                EditSharpConfig.Logger.LogVerbose(
                    $"MediaHasher: failed to persist hash sidecar: {ex.Message}");
            }
            finally
            {
                _sidecarGate.Release();
            }
        }
    }
}