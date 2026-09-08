using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
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
    /// (raw/RLE fixed-rate frames vs. a real decodable video container),
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
    ///
    /// FORMAT V2 — SINGLE SELF-CONTAINED FILE, OPTIONAL PER-FRAME
    /// COMPRESSION (decided in conversation, once the original all-raw,
    /// two-file design had been proven correct and stable on real hardware):
    /// every entry is now exactly ONE .esrp file — no more companion
    /// `.meta.json` — with its metadata embedded and, by default, every
    /// frame's pixels PackBits-RLE-compressed for real on-disk size savings
    /// at negligible per-tick CPU cost (real-world-tested in a separate
    /// application). See ScrubProxyFormat's own VERSION 2 remarks, and
    /// EditSharpConfig.ScrubProxyCompressionScheme for the build-time knob
    /// (default Rle; set to None for the original fully-raw shape). This
    /// class's own on-disk layout changed accordingly: PathsFor now returns
    /// one path, not a (media, meta) pair, and TryLoadExistingAsync/
    /// BuildAsync/EncodeAsync read/write the embedded header+metadata+frame-
    /// index directly rather than a separate JSON file.
    ///
    /// FRAME-PIXEL ENCODING PULLED INTO A PLAIN SYNCHRONOUS HELPER
    /// (EncodeFramePixels, hardening fix, no observed compiler to confirm
    /// against in this environment): `EncodeAsync` used to declare
    /// `ReadOnlySpan&lt;byte&gt; raw = pixmap.GetPixelSpan();` — a ref-struct
    /// local — directly inside its own `async` method body, spanning
    /// several `await`s before and after it in the surrounding loop. Ref
    /// structs can't be held live across an `await` (the compiler would
    /// reject that outright), and older C# versions additionally disallow
    /// declaring one as a local ANYWHERE inside an async method body at
    /// all, even when it's never actually live across a suspension point —
    /// a real, plausible source of a silent build failure on whatever C#
    /// language version this project targets, and not something to leave
    /// unresolved just because it can't be confirmed without a compiler
    /// here. FIX: the span-touching work (GetPixelSpan + RLE-or-raw encode)
    /// now lives entirely inside EncodeFramePixels, a plain, fully
    /// synchronous, non-async method — no `await` anywhere in it, so a
    /// `Span`/`ReadOnlySpan` local is always unambiguously safe there
    /// regardless of language version. EncodeAsync itself now only ever
    /// holds the RETURNED `byte[]` (a normal heap object, never a ref
    /// struct) across its own awaits.
    ///
    /// BUILD FAILURES ARE NOW LOGGED (hardening fix): BuildAndTrackAsync
    /// used to record a build failure ONLY into `LastFailure` (consulted
    /// by GetStatusAsync) — there was no actual log line anywhere for a
    /// build that failed outright, unlike the existing success-path Log()
    /// calls in BuildAsync. Paired with Playback.ScrubToAsync's own new
    /// catch-all logging (see Playback's class remarks, SCRUB FAILURES ARE
    /// NOW LOGGED, NOT SILENT), this is what actually put a message on the
    /// record for the reported "empty scrub proxy folder, no proxy file,
    /// no error shown anywhere" symptom — previously NEITHER layer logged
    /// anything on this path.
    ///
    /// FORMAT V3 — Indexed8 PIXEL FORMAT (decided in conversation, direct
    /// response to a user proposal to trade exact color accuracy for a
    /// large size reduction — see ScrubProxyPixelFormat.Indexed8 and
    /// ColorQuantizer for the full design/reasoning): EncodeFramePixels now
    /// branches on EditSharpConfig.ScrubProxyPixelFormat. For Indexed8,
    /// ColorQuantizer.Quantize builds this frame's own 256-color palette
    /// and per-pixel index plane, which are concatenated (palette raw,
    /// always; indices optionally Rle'd, same as an Rgba8888 frame's own
    /// bytes would be) into the single blob BuildAsync/EncodeAsync already
    /// treat as an opaque per-frame byte sequence — no change needed to
    /// the frame-index/offset bookkeeping in EncodeAsync at all, since that
    /// machinery never cared WHY a frame's length varies, only that it
    /// does. GPU-SHADER DECODE OF THIS FORMAT IS EXPLICITLY OUT OF SCOPE
    /// FOR THIS ROUND — deferred to a separate, later optimization per
    /// direct user instruction ("let's treat the GPU decode piece as a
    /// later step") — for now, ScrubProxyReader.GetFrameAt expands an
    /// Indexed8 frame back to full RGBA8888 entirely on the CPU (see that
    /// method's own remarks), so ScrubFrameSource/Playback need zero
    /// awareness this format exists at all.
    /// </summary>
    internal static class ScrubProxyCache
    {
        private const int CurrentSchemaVersion = 1;
        private const string Extension = "esrp";

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
            ScrubProxyEntry? result = TryLoadExisting(hash);

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

        /// <summary>
        /// See class remarks, BUILD FAILURES ARE NOW LOGGED: any exception
        /// out of BuildAsync is now logged here, in addition to being
        /// recorded into LastFailure (for GetStatusAsync) and rethrown (for
        /// whoever's awaiting InFlight[hash] directly). Previously this
        /// catch block recorded the failure silently with no log line at
        /// all — a caller that doesn't observe the returned Task (e.g. an
        /// opportunistic PrewarmAsync fire-and-forget, or a build kicked off
        /// by a ScrubToAsync call that's itself superseded before it awaits
        /// the result) would never see any evidence a build failed.
        /// </summary>
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
                EditSharpConfig.Logger.LogError(
                    $"ScrubProxyCache: failed to build a scrub proxy for '{sourcePath}': {ex}");
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
        /// of the per-tick scrub path in an earlier fix), and writes the
        /// whole self-contained .esrp file (header + embedded metadata +
        /// frame index + every frame's stored pixels) via EncodeAsync.
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

            ScrubProxyCompressionScheme compressionScheme = EditSharpConfig.ScrubProxyCompressionScheme;
            ScrubProxyPixelFormat pixelFormat = EditSharpConfig.ScrubProxyPixelFormat;

            DecodeHwAccelPlan plan = await FfmpegRunner.GetDecodePlanAsync(sourcePath, hwAccel);

            (string finalPath, string dir) = PathFor(hash);
            Directory.CreateDirectory(dir);

            string tempPath = Path.Combine(dir, $"{hash}.tmp-{Guid.NewGuid():N}.{Extension}");

            var meta = new ScrubProxyMeta
            {
                SchemaVersion = CurrentSchemaVersion,
                SourceHash = hash,
                KnownSourcePaths = { Path.GetFullPath(sourcePath) },
                OriginalWidth = sourceInfo.Width,
                OriginalHeight = sourceInfo.Height,
                TargetShortSideAtBuild = EditSharpConfig.ScrubProxyTargetShortSide,
                CreatedAtUtc = DateTime.UtcNow,
            };

            EditSharpConfig.Logger.Log(
                $"Building scrub proxy for '{sourcePath}' ({width}x{height} @ {sampleRate}/s, {frameCount} " +
                $"frames, pixelFormat={pixelFormat}, compression={compressionScheme})...");
            var sw = Stopwatch.StartNew();

            try
            {
                await EncodeAsync(
                    sourcePath, tempPath, width, height, sampleRate, frameCount, pixelFormat, compressionScheme, meta, plan);
                File.Move(tempPath, finalPath, overwrite: true);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }

            EditSharpConfig.Logger.Log(
                $"Scrub proxy built in {sw.ElapsedMilliseconds}ms -> {finalPath}");

            return new ScrubProxyEntry(finalPath, width, height, sampleRate, frameCount);
        }

        /// <summary>
        /// Decodes `sourcePath` ONCE, resampled to `fps`/`width`x`height`
        /// via SkSourceDecoder's existing persistent-pipe machinery (the
        /// same one real forward playback uses), and writes the complete
        /// self-contained .esrp file to `outputPath` — fixed header, then
        /// the embedded metadata blob, then a placeholder frame-index
        /// region, then every frame's stored (raw or Rle-encoded) bytes
        /// back to back, then a seek-back to fill in the frame index with
        /// each frame's REAL (offset, length) now that they're known — see
        /// ScrubProxyFormat's LAYOUT remarks for the exact byte order this
        /// produces. The seek-back is a plain in-place overwrite of a
        /// region already reserved earlier in this same write, not a
        /// truncation — the file already extends past it by the time the
        /// backpatch runs.
        ///
        /// Per-frame pixel encoding itself is delegated to
        /// EncodeFramePixels — see class remarks, FRAME-PIXEL ENCODING
        /// PULLED INTO A PLAIN SYNCHRONOUS HELPER — so this method's own
        /// loop only ever holds a `byte[]` (never a `Span`/`ReadOnlySpan`)
        /// across its `await stream.WriteAsync(stored)` call.
        /// </summary>
        private static async Task EncodeAsync(
            string sourcePath, string outputPath, int width, int height, int fps, int frameCount,
            ScrubProxyPixelFormat pixelFormat, ScrubProxyCompressionScheme compressionScheme,
            ScrubProxyMeta meta, DecodeHwAccelPlan plan)
        {
            using SkSourceDecoder decoder = SkSourceDecoder.Start(
                sourcePath, 0, fps, width, height, plan, fastOpen: false);

            byte[] metaBytes = ScrubProxyMetaSerializer.SerializeToUtf8Bytes(meta);

            long frameIndexOffset = ScrubProxyFormat.FrameIndexOffset(metaBytes.Length);
            int frameIndexByteSize = frameCount * ScrubProxyFormat.FrameIndexEntrySize;
            long frameDataStartOffset = frameIndexOffset + frameIndexByteSize;

            using var stream = new FileStream(
                outputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20, useAsync: true);

            byte[] header = new byte[ScrubProxyFormat.HeaderSize];
            ScrubProxyFormat.WriteHeader(header, width, height, pixelFormat, compressionScheme, fps, frameCount, metaBytes.Length);
            await stream.WriteAsync(header);

            await stream.WriteAsync(metaBytes);

            // Reserve the frame index region with zeros for now — every
            // entry's real (offset, length) is only known once its frame
            // has actually been written below.
            await stream.WriteAsync(new byte[frameIndexByteSize]);

            var frameIndex = new (long Offset, int Length)[frameCount];
            long cursor = frameDataStartOffset;

            for (int i = 0; i < frameCount; i++)
            {
                using SKImage frame = decoder.NextFrame();

                byte[] stored = EncodeFramePixels(frame, sourcePath, i, width, height, pixelFormat, compressionScheme);

                frameIndex[i] = (cursor, stored.Length);
                await stream.WriteAsync(stored);
                cursor += stored.Length;
            }

            await stream.FlushAsync();

            byte[] indexBytes = new byte[frameIndexByteSize];
            for (int i = 0; i < frameCount; i++)
            {
                ScrubProxyFormat.WriteFrameIndexEntry(
                    indexBytes.AsSpan(i * ScrubProxyFormat.FrameIndexEntrySize, ScrubProxyFormat.FrameIndexEntrySize),
                    frameIndex[i].Offset, frameIndex[i].Length);
            }

            stream.Seek(frameIndexOffset, SeekOrigin.Begin);
            await stream.WriteAsync(indexBytes);
            await stream.FlushAsync();
        }

        /// <summary>
        /// PLAIN, FULLY SYNCHRONOUS — deliberately not async, and never
        /// itself awaited from anywhere — see class remarks, FRAME-PIXEL
        /// ENCODING PULLED INTO A PLAIN SYNCHRONOUS HELPER. Reads one
        /// decoded frame's raw pixels via SKPixmap.GetPixelSpan() (a
        /// ref-struct ReadOnlySpan&lt;byte&gt;, safe here precisely because
        /// nothing in this method ever suspends) and returns this frame's
        /// complete on-disk blob, shaped according to `pixelFormat`:
        ///   - Rgba8888: either the RLE-encoded bytes or a plain heap copy
        ///     of the raw bytes, depending on `compressionScheme` — exactly
        ///     the v2 behavior, unchanged.
        ///   - Indexed8: ColorQuantizer.Quantize builds this frame's own
        ///     256-color palette and per-pixel index plane (see
        ///     ScrubProxyPixelFormat.Indexed8's own remarks); the palette
        ///     is always stored raw, and the index plane is RLE-encoded or
        ///     copied raw depending on `compressionScheme`, same as an
        ///     Rgba8888 frame's own bytes would be. The two pieces are
        ///     concatenated (palette first, at the fixed 1024-byte offset
        ///     ScrubProxyReader expects) into the single returned blob.
        /// `frame`/`sourcePath`/`frameIndex` are used only to produce a
        /// clear exception message on the (rare, but real — see
        /// BuildAndTrackAsync's own catch, now logged) PeekPixels() failure
        /// case; the caller (EncodeAsync) still owns `frame`'s lifetime via
        /// its own `using`.
        ///
        /// SkSourceDecoder's own WrapAsImage always builds its SKImage with
        /// rowBytes == width * 4 (no padding) — GetPixelSpan is therefore
        /// already exactly frameByteSize contiguous bytes, matching what
        /// both ScrubProxyRle.Encode/the raw path and ColorQuantizer.Quantize
        /// expect.
        /// </summary>
        private static byte[] EncodeFramePixels(
            SKImage frame, string sourcePath, int frameIndex, int width, int height,
            ScrubProxyPixelFormat pixelFormat, ScrubProxyCompressionScheme compressionScheme)
        {
            using SKPixmap? pixmap = frame.PeekPixels();
            if (pixmap == null)
                throw new InvalidOperationException(
                    $"Could not read decoded pixels while building a scrub proxy for '{sourcePath}' (frame {frameIndex}).");

            ReadOnlySpan<byte> raw = pixmap.GetPixelSpan();

            if (pixelFormat == ScrubProxyPixelFormat.Indexed8)
            {
                byte[] palette = new byte[ScrubProxyFormat.IndexedPaletteByteSize];
                byte[] indices = new byte[width * height];
                ColorQuantizer.Quantize(raw, width, height, palette, indices);

                byte[] storedIndices = compressionScheme == ScrubProxyCompressionScheme.Rle
                    ? ScrubProxyRle.Encode(indices)
                    : indices;

                byte[] combined = new byte[palette.Length + storedIndices.Length];
                palette.CopyTo(combined, 0);
                storedIndices.CopyTo(combined, palette.Length);
                return combined;
            }

            return compressionScheme == ScrubProxyCompressionScheme.Rle
                ? ScrubProxyRle.Encode(raw)
                : raw.ToArray();
        }

        /// <summary>
        /// Looks up an existing entry on disk by hash — the read side of
        /// the cache. A confirmed hit is memoized into ResolvedEntries; a
        /// miss (a missing file, a wrong/older format version, an
        /// unreadable/mismatched embedded metadata blob) is treated as a
        /// plain miss, silently rebuildable, never an error surfaced to the
        /// caller that stumbled onto it. Fully synchronous now — reading
        /// the header/metadata is a couple of small positioned reads, no
        /// real async I/O worth awaiting — but kept easy to call from the
        /// async call sites above.
        /// </summary>
        private static ScrubProxyEntry? TryLoadExisting(string hash)
        {
            if (ResolvedEntries.TryGetValue(hash, out ScrubProxyEntry memoized))
                return memoized;

            (string mediaPath, _) = PathFor(hash);

            if (!File.Exists(mediaPath))
                return null;

            ScrubProxyFormat.Header header;
            ScrubProxyMeta meta;
            try
            {
                (header, meta) = ScrubProxyReader.ReadHeaderAndMeta(mediaPath);
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogVerbose(
                    $"ScrubProxyCache: '{mediaPath}' is unreadable or a different format version, treating as a " +
                    $"miss: {ex.Message}");
                return null;
            }

            if (meta.SchemaVersion != CurrentSchemaVersion || meta.SourceHash != hash)
            {
                EditSharpConfig.Logger.LogVerbose(
                    $"ScrubProxyCache: embedded metadata for '{hash}' is stale or mismatched, treating as a miss.");
                return null;
            }

            var entry = new ScrubProxyEntry(mediaPath, header.Width, header.Height, header.SampleRate, header.FrameCount);
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

        /// <summary>
        /// One self-contained .esrp file per entry now (see class remarks,
        /// FORMAT V2) — no separate meta-file path any more. Still returns
        /// the containing directory alongside it, since BuildAsync needs to
        /// CreateDirectory it before writing.
        /// </summary>
        private static (string MediaPath, string Directory) PathFor(string hash)
        {
            // Same sharded content-addressable-storage layout OptimizedMediaCache
            // uses — see its own PathsFor remarks.
            string shard = hash[..2];
            string dir = Path.Combine(EditSharpConfig.ScrubProxyDirectory, shard);

            string mediaPath = Path.Combine(dir, $"{hash}.{Extension}");

            return (mediaPath, dir);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best-effort cleanup of a failed build's partial temp file */ }
        }

        private static string NormalizeSourcePath(string path) => Path.GetFullPath(path).ToLowerInvariant();
    }
}