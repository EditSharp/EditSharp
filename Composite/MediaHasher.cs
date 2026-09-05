using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EditSharp;

namespace EditSharp.Composite
{
    /// <summary>
    /// Computes a fast, bounded-I/O checksum of a source's contents — the
    /// key OptimizedMediaCache uses to find a source's optimized media
    /// regardless of where the file currently lives on disk (see
    /// OptimizedMediaCache's own remarks for why content, not path, is the
    /// cache key: a moved or duplicated source still resolves to the same
    /// cache entry).
    ///
    /// SAMPLED HASH, NOT FULL-FILE — REVERSED IN CONVERSATION FROM THIS
    /// CLASS'S ORIGINAL DESIGN: a full-file SHA-256 was tried first
    /// (unconditional correctness — two different files could never share
    /// a cache entry), but measured in practice at up to ~2s per video on
    /// real footage, which ate nearly all of what optimized media is
    /// supposed to save. This method instead hashes file LENGTH plus a
    /// fixed, small number of fixed-size byte samples spread across the
    /// file (see SampleCount/SampleChunkSize below) — bounded I/O
    /// regardless of file size (at most SampleCount*SampleChunkSize bytes
    /// ever read, ~512 KiB total, vs. the entire file), so a cold hash on a
    /// multi-gigabyte source now costs a handful of seeks instead of
    /// reading gigabytes.
    ///
    /// TRADEOFF, NAMED NOT HIDDEN: this is no longer an unconditional
    /// correctness guarantee. Two different files that happen to share the
    /// same length and identical bytes at every sampled offset would
    /// collide and incorrectly share a cache entry — vanishingly unlikely
    /// for real, independently-created video files (matching length AND
    /// matching bytes at ~8 spread-out points is not the kind of thing
    /// real footage does by accident), but not impossible the way a
    /// full-file hash's guarantee was. Chosen anyway because the whole
    /// point of this cache is speed, and a check that costs more than it
    /// saves defeats its own purpose — see MediaHasher's remarks history
    /// for the reversal.
    ///
    /// MEMOIZED TWO WAYS (unchanged from before this reversal):
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
    ///      same project — every source would pay a re-hash on every app
    ///      launch otherwise. The sidecar remembers (path, length,
    ///      lastWriteTimeUtc) -> hash across runs; a file whose size/mtime
    ///      still match what's recorded skips hashing entirely, and any
    ///      mismatch (the file changed, or a genuinely different file now
    ///      sits at that path) falls through to a real re-hash rather than
    ///      trusting stale data. SIDECAR FILENAME BUMPED (hash-index-v2.json,
    ///      was hash-index.json) specifically because this reversal changes
    ///      what a "hash" for a given file actually IS — an old sidecar
    ///      full of full-file hashes must never be read back in as if it
    ///      held sampled ones (they're not comparable, and mixing them
    ///      would just mean some files silently miss the cache they should
    ///      hit). Starting a fresh sidecar file is simpler and safer than
    ///      trying to version individual entries.
    ///
    /// NOT a security-sensitive hash — collision resistance here is about
    /// accidentally reusing a different file's optimized media, not
    /// defending against a deliberate adversary, so SHA-256 is chosen for
    /// being fast, built into .NET with no extra dependency, and more than
    /// sufficient collision resistance for this purpose over the (length +
    /// sampled bytes) input this class actually feeds it.
    /// </summary>
    internal static class MediaHasher
    {
        /// <summary>
        /// How many fixed-size chunks are sampled from a large file, spread
        /// evenly from its first byte to its last (inclusive of both
        /// endpoints — see SampleOffsets). 8 was picked as "enough spread
        /// across a real video file's container structure to make an
        /// accidental collision practically impossible, while staying a
        /// small, constant, bounded amount of I/O" — not tuned against a
        /// measured collision rate, since no two genuinely different real
        /// files have been observed to collide under this scheme; flagged
        /// as a value chosen by reasoning rather than by measurement, same
        /// honesty standard this codebase applies to its own unverified
        /// values elsewhere.
        /// </summary>
        private const int SampleCount = 8;

        /// <summary>
        /// Size of each sampled chunk — 64 KiB. Large enough that a chunk
        /// captures real structure (not just a handful of bytes that could
        /// plausibly repeat), small enough that SampleCount * SampleChunkSize
        /// (512 KiB total) stays trivial to read even on a slow disk.
        /// </summary>
        private const int SampleChunkSize = 64 * 1024;

        /// <summary>
        /// Below this size, sampling would already touch a large fraction
        /// of the file anyway (and risks overlapping chunks — see
        /// SampleOffsets), so the whole file is hashed directly instead.
        /// This is also already fast in absolute terms for anything this
        /// small, so there's nothing to trade off by doing the simpler
        /// thing here.
        /// </summary>
        private const long FullHashThreshold = SampleCount * SampleChunkSize;

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
        /// Lower-case hex SHA-256 over `path`'s length and a small, fixed
        /// set of sampled byte ranges (see the class remarks for why this
        /// is sampled rather than full-file, and the tradeoff that implies).
        /// Throws FileNotFoundException if the file doesn't exist — callers
        /// that need "missing file" to be a soft failure should check
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

            string hash = await HashFileAsync(path, info.Length, ct);

            _inMemory[key] = hash;
            await WriteSidecarAsync(key, hash, ct);

            return hash;
        }

        private static async Task<string> HashFileAsync(string path, long length, CancellationToken ct)
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1 << 16, useAsync: true);

            if (length <= FullHashThreshold)
            {
                // Small file — sampling would overlap itself and buys
                // nothing; just hash it all directly. Static
                // SHA256.HashDataAsync (.NET 6+) streams the file rather
                // than requiring it all resident at once.
                byte[] wholeFileHash = await SHA256.HashDataAsync(stream, ct);
                return Convert.ToHexString(wholeFileHash).ToLowerInvariant();
            }

            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            // Length goes into the hash FIRST and explicitly — two files
            // that happen to agree at every sampled offset but differ in
            // overall size (e.g. one is a truncated/extended copy of the
            // other) must still hash differently; without this, only the
            // sampled bytes would be compared and a length difference
            // outside the sampled ranges could go unnoticed.
            sha256.AppendData(BitConverter.GetBytes(length));

            byte[] buffer = new byte[SampleChunkSize];

            foreach (long offset in SampleOffsets(length))
            {
                stream.Seek(offset, SeekOrigin.Begin);

                int read = await ReadFullyAsync(stream, buffer, SampleChunkSize, ct);

                // The offset itself is also part of the hashed data (not
                // just the bytes read from it) — otherwise two files whose
                // content is simply shifted relative to each other (the
                // same bytes, at different absolute positions) could
                // collide despite being genuinely different files.
                sha256.AppendData(BitConverter.GetBytes(offset));
                sha256.AppendData(buffer, 0, read);
            }

            byte[] hashBytes = sha256.GetHashAndReset();
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        /// <summary>
        /// SampleCount offsets spread evenly from byte 0 to `length -
        /// SampleChunkSize` inclusive — the first sample always starts at
        /// the very beginning of the file, the last always ends at the
        /// very last byte, and the rest are evenly spaced between them.
        /// Deliberately includes both endpoints: a format's own header/
        /// metadata often concentrates right at the start, and a trailing
        /// index/moov atom often sits right at the end, so those two fixed
        /// points are the single highest-value bytes to always include
        /// alongside the evenly-spread middle samples.
        ///
        /// Only called when length > FullHashThreshold, which guarantees
        /// `length - SampleChunkSize` is positive and every offset produced
        /// here has a full SampleChunkSize worth of room to read from
        /// without running past end of file.
        /// </summary>
        private static IEnumerable<long> SampleOffsets(long length)
        {
            long maxOffset = length - SampleChunkSize;

            for (int i = 0; i < SampleCount; i++)
            {
                // Integer math ordered to avoid overflow on a very large
                // file: (i * maxOffset) could exceed long range for i and
                // maxOffset both large, so divide down via double instead
                // — sub-chunk-sized rounding error here is irrelevant,
                // this only has to land somewhere inside the file, not at
                // an exact byte.
                long offset = SampleCount == 1
                    ? 0
                    : (long)((double)i / (SampleCount - 1) * maxOffset);

                yield return offset;
            }
        }

        /// <summary>
        /// Reads up to `count` bytes into `buffer`, looping until either
        /// `count` bytes have been read or the stream ends — a single
        /// Stream.ReadAsync call is permitted to return fewer bytes than
        /// requested even mid-file, not just at EOF, so this can't assume
        /// one call is enough the way a naive read might.
        /// </summary>
        private static async Task<int> ReadFullyAsync(
            Stream stream, byte[] buffer, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total, count - total), ct);
                if (read == 0) break; // real EOF — shouldn't happen given SampleOffsets' guarantee, but tolerated rather than assumed
                total += read;
            }
            return total;
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
            Path.Combine(EditSharpConfig.OptimizedMediaDirectory, "hash-index-v2.json");

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
                string tempPath = Path.Combine(
                    Path.GetDirectoryName(SidecarPath) ?? "",
                    $"{Path.GetFileName(SidecarPath)}.tmp-{Guid.NewGuid():N}");

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
                // pays a re-hash again next run.
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