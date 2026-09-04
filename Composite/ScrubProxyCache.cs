using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EditSharp;
using SkiaSharp;

namespace EditSharp.Composite
{
    /// <summary>
    /// Where a lookup or a build attempt currently stands for one source's
    /// scrub proxy — mirrors OptimizedMediaStatus, kept as its own enum
    /// rather than reused since it's a distinct cache with its own build
    /// pipeline, not a variant of optimized media.
    /// </summary>
    public enum ScrubProxyStatus
    {
        NotCached,
        Building,
        Ready,
        Failed,
    }

    /// <summary>
    /// One source's ready-to-use scrub proxy: where its .esrp file lives on
    /// disk and the header facts ScrubProxyReader needs to interpret it
    /// (also duplicated here so a caller doesn't have to open the file just
    /// to know its shape).
    /// </summary>
    public readonly record struct ScrubProxyEntry(string Path, int Width, int Height, double SampleRate, int FrameCount);

    /// <summary>
    /// Persistent, content-addressed cache of raw, decoder-less scrub
    /// proxies (see ScrubProxyFormat for the file shape) — DECIDED IN
    /// CONVERSATION, replacing the earlier ffmpeg-keyframe-decode approach
    /// to scrubbing/reverse playback entirely, after two rounds of real-
    /// world testing (see Playback's own class remarks) showed that
    /// spawning ANY ffmpeg process per scrub tick — GPU or software — could
    /// not be made both fast and crash-safe under a fast scrub drag.
    ///
    /// WHY THIS EXISTS: the same structural problem OptimizedMediaCache
    /// solves for forward playback (a source's own codec/GOP structure can
    /// make an arbitrary seek expensive) applies even harder to scrubbing,
    /// which needs MANY arbitrary seeks per second with zero tolerance for
    /// per-tick latency. OptimizedMediaCache's answer (an intra-only ffmpeg-
    /// decodable proxy) still pays a real decode — cheap, but not free, and
    /// not zero-process — on every tick. This cache's answer is stronger:
    /// a proxy with NO decode step at all. See ScrubProxyFormat's own
    /// remarks for the file shape that makes that possible.
    ///
    /// DELIBERATELY A SEPARATE CACHE FROM OptimizedMediaCache, NOT A THIRD
    /// QUALITY TIER OF IT: different purpose (scrub preview, never used by
    /// Render/* or real playback — see ScrubFrameSource), different shape
    /// (raw fixed-rate frames vs. a real decodable video container),
    /// different size target (a few hundred pixels vs. up to 4K) — sharing
    /// one cache/directory/schema would only couple two things that change
    /// for unrelated reasons.
    ///
    /// CONTENT-ADDRESSED, KEYED BY MediaHasher'S HASH — same reasoning as
    /// OptimizedMediaCache: a source that's renamed, moved, or duplicated
    /// under a second name still resolves to the same cache entry.
    ///
    /// FULLY OPPORTUNISTIC AT THE CALL SITE FOR PrewarmAsync, BUT BLOCKING
    /// FOR Playback's OWN SCRUB-SESSION SETUP — a deliberate difference from
    /// OptimizedMediaCache (whose Playback/Renderer call sites use the non-
    /// blocking TryGetAsync and never trigger a build themselves). Per the
    /// decided behavior: missing proxies are built when a scrub/reverse
    /// session actually starts (Playback.BuildScrubSessionAsync /
    /// ReverseVideoLoopAsync call GetOrBuildAsync, blocking that session's
    /// startup on the build), with PrewarmAsync offered as the opt-in way
    /// to pay that cost ahead of time instead (e.g. right after import).
    /// This is different from optimized media's build-competes-with-
    /// playback concern because a scrub proxy build is a ONE-TIME, bounded,
    /// low-resolution linear decode pass — not something worth silently
    /// skipping the way a multi-minute 4K optimized-media encode would be.
    ///
    /// MEMOIZATION shape is a direct copy of OptimizedMediaCache's (in-flight
    /// build coalescing, last-failure tracking for GetStatusAsync, a
    /// resolved-entry cache keyed by hash, and a source-identity cache keyed
    /// on (path, length, mtime) that skips even the hash computation for a
    /// file this process has already resolved) — same problem, same fix,
    /// see OptimizedMediaCache's own remarks for the fuller reasoning behind
    /// each layer.
    /// </summary>
    internal static class ScrubProxyCache
    {
        private const int CurrentSchemaVersion = 1;
        private const string Extension = "esrp";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static readonly ConcurrentDictionary<string, Task<ScrubProxyEntry>> InFlight = new();
        private static readonly ConcurrentDictionary<string, Exception> LastFailure = new();
        private static readonly ConcurrentDictionary<string, ScrubProxyEntry> ResolvedEntries = new();

        private static readonly ConcurrentDictionary<(string Path, long Length, long LastWriteTimeUtcTicks), ScrubProxyEntry>
            SourceResolutionCache = new();

        /// <summary>
        /// Returns a ready-to-use scrub proxy for `sourcePath`, building it
        /// first if no cache entry exists yet — BLOCKS the caller until the
        /// build finishes (or fails). This is what Playback's own scrub/
        /// reverse session setup calls — see the class remarks on why that's
        /// the right default here, unlike OptimizedMediaCache's playback
        /// call sites.
        /// </summary>
        public static async Task<ScrubProxyEntry> GetOrBuildAsync(
            string sourcePath, HardwareAccelerator hwAccel, CancellationToken ct = default)
        {
            ScrubProxyEntry? existing = await TryGetAsync(sourcePath, ct);
            if (existing != null) return existing.Value;

            string hash = await MediaHasher.ComputeAsync(sourcePath, ct);

            Task<ScrubProxyEntry> build = InFlight.GetOrAdd(
                hash, _ => BuildAndTrackAsync(sourcePath, hash, hwAccel));

            // .WaitAsync(ct) — this caller's own cancellation stops waiting
            // without cancelling a build another caller (or Playback's own
            // memoized _scrubSetupTask, once started) may also be awaiting.
            // Same "cancel the wait, not the shared work" split KeyframeIndex
            // and Playback's own scrub-session setup already used.
            return await build.WaitAsync(ct);
        }

        /// <summary>
        /// Starts building a scrub proxy for `sourcePath` ahead of need —
        /// the "generate proxies beforehand" convenience entry point a
        /// consumer app can call right after import, mirroring
        /// OptimizedMediaCache.PrewarmAsync. Safe to call fire-and-forget;
        /// failures surface later via GetStatusAsync rather than as an
        /// unobserved exception.
        /// </summary>
        public static Task<ScrubProxyEntry> PrewarmAsync(
            string sourcePath, HardwareAccelerator hwAccel, CancellationToken ct = default) =>
            GetOrBuildAsync(sourcePath, hwAccel, ct);

        /// <summary>
        /// Non-blocking lookup — returns an existing entry if one is already
        /// built and on disk, or null otherwise. Never starts a build.
        /// </summary>
        public static async Task<ScrubProxyEntry?> TryGetAsync(string sourcePath, CancellationToken ct = default)
        {
            var info = new FileInfo(sourcePath);
            if (info.Exists)
            {
                var sourceKey = (NormalizeSourcePath(sourcePath), info.Length, info.LastWriteTimeUtc.Ticks);
                if (SourceResolutionCache.TryGetValue(sourceKey, out ScrubProxyEntry memoized))
                    return memoized;
            }

            string hash = await MediaHasher.ComputeAsync(sourcePath, ct);
            ScrubProxyEntry? result = await TryLoadExistingAsync(hash);

            if (result != null && info.Exists)
            {
                var sourceKey = (NormalizeSourcePath(sourcePath), info.Length, info.LastWriteTimeUtc.Ticks);
                SourceResolutionCache[sourceKey] = result.Value;
            }

            return result;
        }

        public static async Task<ScrubProxyStatus> GetStatusAsync(string sourcePath, CancellationToken ct = default)
        {
            ScrubProxyEntry? existing = await TryGetAsync(sourcePath, ct);
            if (existing != null) return ScrubProxyStatus.Ready;

            string hash = await MediaHasher.ComputeAsync(sourcePath, ct);

            if (InFlight.TryGetValue(hash, out Task<ScrubProxyEntry>? task))
                return task.IsFaulted ? ScrubProxyStatus.Failed : ScrubProxyStatus.Building;

            if (LastFailure.ContainsKey(hash))
                return ScrubProxyStatus.Failed;

            return ScrubProxyStatus.NotCached;
        }

        private static async Task<ScrubProxyEntry> BuildAndTrackAsync(string sourcePath, string hash, HardwareAccelerator hwAccel)
        {
            try
            {
                ScrubProxyEntry entry = await BuildAsync(sourcePath, hash, hwAccel);
                LastFailure.TryRemove(hash, out _);
                ResolvedEntries[hash] = entry;

                var info = new FileInfo(sourcePath);
                if (info.Exists)
                    SourceResolutionCache[(NormalizeSourcePath(sourcePath), info.Length, info.LastWriteTimeUtc.Ticks)] = entry;

                return entry;
            }
            catch (Exception ex)
            {
                LastFailure[hash] = ex;
                throw;
            }
            finally
            {
                InFlight.TryRemove(hash, out _);
            }
        }

        /// <summary>
        /// The actual build: probes the source, computes the proxy's target
        /// size and frame count, decodes+resamples the whole source ONCE via
        /// the existing persistent SkSourceDecoder pipe (GPU-eligible — see
        /// class remarks and Playback's own — this is a single one-time
        /// linear pass, not a per-tick call, so it doesn't carry the per-
        /// tick GPU-session-exhaustion risk that got GPU decode pulled out
        /// of the per-tick scrub path in the previous fix), and writes every
        /// resampled frame's raw pixels straight to the .esrp file.
        /// </summary>
        private static async Task<ScrubProxyEntry> BuildAsync(string sourcePath, string hash, HardwareAccelerator hwAccel)
        {
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException(
                    $"Cannot build a scrub proxy — source not found: '{sourcePath}'", sourcePath);

            MediaInfo sourceInfo = await MediaProbe.ProbeAsync(sourcePath);

            if (!sourceInfo.HasVideo)
                throw new InvalidOperationException($"'{sourcePath}' has no video stream to build a scrub proxy from.");

            if (sourceInfo.Duration is not { } duration || duration <= TimeSpan.Zero)
                throw new InvalidOperationException(
                    $"'{sourcePath}' has no readable duration — cannot size a scrub proxy for it.");

            (int width, int height) = ComputeProxySize(
                sourceInfo.Width, sourceInfo.Height, EditSharpConfig.ScrubProxyTargetShortSide);

            // The configured sample rate is rounded to an integer ONCE here
            // and used consistently for BOTH the decode's own fps-conform
            // filter and the stored header value — a mismatch between the
            // two would desync GetFrameAt's `seconds * SampleRate` math from
            // what was actually decoded and stored.
            int sampleRate = Math.Max(1, (int)Math.Round(EditSharpConfig.ScrubProxySampleRate));
            int frameCount = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds * sampleRate));

            DecodeHwAccelPlan plan = await FfmpegRunner.GetDecodePlanAsync(sourcePath, hwAccel);

            (string finalPath, string metaPath, string dir) = PathsFor(hash);
            Directory.CreateDirectory(dir);

            string tempPath = Path.Combine(dir, $"{hash}.tmp-{Guid.NewGuid():N}.{Extension}");

            EditSharpConfig.Logger.Log(
                $"Building scrub proxy for '{sourcePath}' ({width}x{height} @ {sampleRate}/s, {frameCount} frames)...");
            var sw = Stopwatch.StartNew();

            try
            {
                await EncodeAsync(sourcePath, tempPath, width, height, sampleRate, frameCount, plan);
                File.Move(tempPath, finalPath, overwrite: true);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }

            var meta = new ScrubProxyMeta
            {
                SchemaVersion = CurrentSchemaVersion,
                SourceHash = hash,
                KnownSourcePaths = { Path.GetFullPath(sourcePath) },
                OriginalWidth = sourceInfo.Width,
                OriginalHeight = sourceInfo.Height,
                Width = width,
                Height = height,
                SampleRate = sampleRate,
                FrameCount = frameCount,
                TargetShortSideAtBuild = EditSharpConfig.ScrubProxyTargetShortSide,
                CreatedAtUtc = DateTime.UtcNow,
            };
            await WriteMetaAsync(metaPath, meta);

            EditSharpConfig.Logger.Log(
                $"Scrub proxy built in {sw.ElapsedMilliseconds}ms -> {finalPath}");

            return new ScrubProxyEntry(finalPath, width, height, sampleRate, frameCount);
        }

        /// <summary>
        /// Decodes `sourcePath` ONCE, resampled to `fps`/`width`x`height`
        /// via SkSourceDecoder's existing persistent-pipe machinery (the
        /// same one real forward playback uses), and writes the header plus
        /// every frame's raw RGBA8888 pixels to `outputPath` sequentially —
        /// exactly the layout ScrubProxyFormat/ScrubProxyReader expect.
        /// </summary>
        private static async Task EncodeAsync(
            string sourcePath, string outputPath, int width, int height, int fps, int frameCount, DecodeHwAccelPlan plan)
        {
            using SkSourceDecoder decoder = SkSourceDecoder.Start(
                sourcePath, 0, fps, width, height, plan, fastOpen: false);

            using var stream = new FileStream(
                outputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20, useAsync: true);

            byte[] header = new byte[ScrubProxyFormat.HeaderSize];
            ScrubProxyFormat.WriteHeader(header, width, height, fps, frameCount);
            await stream.WriteAsync(header);

            for (int i = 0; i < frameCount; i++)
            {
                using SKImage frame = decoder.NextFrame();

                using SKPixmap? pixmap = frame.PeekPixels();
                if (pixmap == null)
                    throw new InvalidOperationException(
                        $"Could not read decoded pixels while building a scrub proxy for '{sourcePath}' (frame {i}).");

                // SkSourceDecoder's own WrapAsImage always builds its SKImage
                // with rowBytes == width * 4 (no padding) — GetPixelSpan is
                // therefore already exactly frameByteSize contiguous bytes,
                // matching ScrubProxyFormat's on-disk frame layout with no
                // repacking needed.
                await stream.WriteAsync(pixmap.GetPixelSpan().ToArray());
            }

            await stream.FlushAsync();
        }

        /// <summary>
        /// Looks up an existing entry on disk by hash — the read side of
        /// the cache. A confirmed hit is memoized into ResolvedEntries; a
        /// miss (missing files, unreadable/stale/mismatched meta, or a
        /// header that doesn't parse) is treated as a plain miss, silently
        /// rebuildable, never an error surfaced to the caller that stumbled
        /// onto it.
        /// </summary>
        private static async Task<ScrubProxyEntry?> TryLoadExistingAsync(string hash)
        {
            if (ResolvedEntries.TryGetValue(hash, out ScrubProxyEntry memoized))
                return memoized;

            (string mediaPath, string metaPath, _) = PathsFor(hash);

            if (!File.Exists(mediaPath) || !File.Exists(metaPath))
                return null;

            ScrubProxyMeta? meta;
            try
            {
                string json = await File.ReadAllTextAsync(metaPath);
                meta = JsonSerializer.Deserialize<ScrubProxyMeta>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogVerbose(
                    $"ScrubProxyCache: meta for '{hash}' unreadable, treating as a miss: {ex.Message}");
                return null;
            }

            if (meta == null || meta.SchemaVersion != CurrentSchemaVersion || meta.SourceHash != hash)
            {
                EditSharpConfig.Logger.LogVerbose(
                    $"ScrubProxyCache: meta for '{hash}' is stale or mismatched, treating as a miss.");
                return null;
            }

            var entry = new ScrubProxyEntry(mediaPath, meta.Width, meta.Height, meta.SampleRate, meta.FrameCount);
            ResolvedEntries[hash] = entry;
            return entry;
        }

        /// <summary>
        /// The proxy's target size: `targetShortSide` on whichever axis is
        /// the source's own SHORT side (handles portrait/vertical sources
        /// correctly, not just landscape — see class remarks on the
        /// "fixed short-side, aspect-preserved" decision), the other axis
        /// scaled proportionally, aspect preserved, never upscaled past
        /// native. Rounded to even on both axes purely for consistency with
        /// this codebase's other proxy-sizing convention (OptimizedMediaCache.
        /// ComputeTargetSize) — RGBA8888 has no chroma-subsampling
        /// constraint that actually requires it, unlike that method's yuv422p
        /// target.
        /// </summary>
        private static (int Width, int Height) ComputeProxySize(int nativeWidth, int nativeHeight, int targetShortSide)
        {
            if (nativeWidth <= 0 || nativeHeight <= 0)
                throw new InvalidOperationException(
                    "Cannot compute a scrub proxy size for a source with no readable dimensions.");

            int shortSide = Math.Min(nativeWidth, nativeHeight);
            double fit = shortSide <= targetShortSide ? 1.0 : (double)targetShortSide / shortSide;

            int width = RoundToEven(Math.Max(2, (int)Math.Round(nativeWidth * fit)));
            int height = RoundToEven(Math.Max(2, (int)Math.Round(nativeHeight * fit)));

            return (width, height);
        }

        private static int RoundToEven(int value) => value % 2 == 0 ? value : value + 1;

        private static (string MediaPath, string MetaPath, string Directory) PathsFor(string hash)
        {
            // Same sharded content-addressable-storage layout OptimizedMediaCache
            // uses — see its own PathsFor remarks.
            string shard = hash[..2];
            string dir = Path.Combine(EditSharpConfig.ScrubProxyDirectory, shard);

            string mediaPath = Path.Combine(dir, $"{hash}.{Extension}");
            string metaPath = Path.Combine(dir, $"{hash}.meta.json");

            return (mediaPath, metaPath, dir);
        }

        private static async Task WriteMetaAsync(string finalPath, ScrubProxyMeta meta)
        {
            string tempPath = Path.Combine(
                Path.GetDirectoryName(finalPath) ?? "",
                $"{Path.GetFileName(finalPath)}.tmp-{Guid.NewGuid():N}");
            string json = JsonSerializer.Serialize(meta, JsonOptions);

            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, finalPath, overwrite: true);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best-effort cleanup of a failed build's partial temp file */ }
        }

        private static string NormalizeSourcePath(string path) => Path.GetFullPath(path).ToLowerInvariant();
    }
}