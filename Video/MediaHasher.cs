using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EditSharp;

namespace EditSharp.Video
{
    /// <summary>A fast content hash of a media file, so a moved, renamed or copied file finds the same proxy.</summary>
    /// <remarks>
    /// SHA-256 over the file's length and eight 64 KiB samples spread from its
    /// first byte to its last, each with its offset: at most 512 KiB read
    /// however large the file (a full-file hash took up to two seconds per
    /// video). Two different files would have to match in length and at every
    /// sample to collide. Smaller files are hashed whole. Results are remembered
    /// in memory and, across runs, in hash-index-v2.json under
    /// <see cref="EditSharpConfig.ProxyDirectory"/>, keyed by path, size and
    /// write time; a file that changed is hashed again.
    /// </remarks>
    internal static class MediaHasher
    {
        //chosen by reasoning, not measured; no two real files have been seen to collide
        private const int SampleCount = 8;

        private const int SampleChunkSize = 64 * 1024;

        //below this, samples would overlap, so the whole file is hashed
        private const long FullHashThreshold = SampleCount * SampleChunkSize;

        private readonly record struct CacheKey(string Path, long Length, long LastWriteTimeUtcTicks);

        private sealed class SidecarEntry
        {
            public long Length { get; set; }
            public long LastWriteTimeUtcTicks { get; set; }
            public string Hash { get; set; } = "";
        }

        private static readonly ConcurrentDictionary<CacheKey, string> _inMemory = new();

        //guards the sidecar within this process. Across processes the last writer wins, losing at most
        //that process's new entries; the file is replaced whole, so it's never corrupted
        private static readonly SemaphoreSlim _sidecarGate = new(1, 1);
        private static Dictionary<string, SidecarEntry>? _sidecar;

        //lower-case hex SHA-256 of the file's length and samples; throws FileNotFoundException for a missing file
        public static async Task<string> ComputeAsync(string path, CancellationToken ct = default)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                throw new FileNotFoundException($"Cannot hash '{path}': file not found.", path);

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
                //small file: hash all of it, streamed
                byte[] wholeFileHash = await SHA256.HashDataAsync(stream, ct);
                return Convert.ToHexString(wholeFileHash).ToLowerInvariant();
            }

            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            //length first, so files that match at every sample but differ in size still differ
            sha256.AppendData(BitConverter.GetBytes(length));

            byte[] buffer = new byte[SampleChunkSize];

            foreach (long offset in SampleOffsets(length))
            {
                stream.Seek(offset, SeekOrigin.Begin);

                int read = await ReadFullyAsync(stream, buffer, SampleChunkSize, ct);

                //the offset too, so the same bytes at a different position hash differently
                sha256.AppendData(BitConverter.GetBytes(offset));
                sha256.AppendData(buffer, 0, read);
            }

            byte[] hashBytes = sha256.GetHashAndReset();
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        //offsets from 0 to length - SampleChunkSize, both ends included: headers sit at the start and
        //an index (a moov atom) often at the end
        private static IEnumerable<long> SampleOffsets(long length)
        {
            long maxOffset = length - SampleChunkSize;

            for (int i = 0; i < SampleCount; i++)
            {
                //through double, since i * maxOffset can overflow a long; the result only has to land inside the file
                long offset = SampleCount == 1
                    ? 0
                    : (long)((double)i / (SampleCount - 1) * maxOffset);

                yield return offset;
            }
        }

        //reads until `count` bytes or the end of the stream; one ReadAsync may return fewer mid-file
        private static async Task<int> ReadFullyAsync(
            Stream stream, byte[] buffer, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total, count - total), ct);
                if (read == 0) break; //the end of the file; SampleOffsets keeps samples inside it, but a short read is tolerated
                total += read;
            }
            return total;
        }

        private static string NormalizePath(string path) =>
            //Windows paths are case-insensitive, so the same file under a differently cased path shares its entry
            Path.GetFullPath(path).ToLowerInvariant();

        private static string SidecarPath =>
            Path.Combine(EditSharpConfig.ProxyDirectory, "hash-index-v2.json");

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
                    //an unreadable sidecar counts as empty; the next save rewrites it
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

                Directory.CreateDirectory(EditSharpConfig.ProxyDirectory);
                string tempPath = Path.Combine(
                    Path.GetDirectoryName(SidecarPath) ?? "",
                    $"{Path.GetFileName(SidecarPath)}.tmp-{Guid.NewGuid():N}");

                await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(_sidecar), ct);

                //atomic on one volume, so a reader never sees half a file
                File.Move(tempPath, SidecarPath, overwrite: true);
            }
            catch (Exception ex)
            {
                //the sidecar only saves re-hashing next run, so failing to write it isn't an error
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