using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    /// DELIBERATELY A SEPARATE CACHE FROM OptimizedMediaCache — different
    /// purpose (scrub preview, never used by Render/* or real playback —
    /// see ScrubFrameSource), different shape, different size target.
    ///
    /// CONTENT-ADDRESSED, KEYED BY MediaHasher'S HASH — a source that's
    /// renamed, moved, or duplicated under a second name still resolves to
    /// the same cache entry.
    ///
    /// CALLER CONTRACT: Playback's scrub/reverse session setup fires each
    /// referenced source's resolution via Task.Run and moves on, showing
    /// the offline placeholder for whatever isn't ready yet (see
    /// ScrubFrameSource). GetOrBuildAsync itself still blocks ITS OWN
    /// caller until the build finishes — that contract stays true for
    /// PrewarmScrubProxiesAsync (still fully awaited by design) and for the
    /// fire-and-forget Task.Run wrapper Playback uses instead of awaiting
    /// it directly.
    ///
    /// MEMOIZATION shape is a direct copy of OptimizedMediaCache's (in-flight
    /// build coalescing, last-failure tracking for GetStatusAsync, a
    /// resolved-entry cache keyed by hash, and a source-identity cache keyed
    /// on (path, length, mtime) that skips even the hash computation for a
    /// file this process has already resolved).
    ///
    /// IN-FLIGHT BUILD COALESCING HARDENED AGAINST A REAL GetOrAdd RACE
    /// (fixed here, real-world regression: "it attempted to build 7
    /// identical files"): `InFlight` stores `Lazy&lt;Task&lt;ScrubProxyEntry&gt;&gt;`
    /// instead of `Task&lt;ScrubProxyEntry&gt;` directly, so a `GetOrAdd` race
    /// under contention can only ever construct (and discard) unused `Lazy`
    /// wrappers — the actual build only starts the first time `.Value` is
    /// read on whichever `Lazy` the dictionary settles on keeping, and
    /// `Lazy`'s own default thread-safety mode guarantees that read
    /// triggers the factory at most once.
    ///
    /// FORMAT V2 — SINGLE SELF-CONTAINED FILE, OPTIONAL PER-FRAME
    /// COMPRESSION: every entry is one .esrp file — metadata embedded, no
    /// companion file — with every frame's pixels optionally compressed
    /// (see EditSharpConfig.ScrubProxyCompressionScheme).
    ///
    /// FRAME-PIXEL ENCODING IS A PLAIN SYNCHRONOUS HELPER
    /// (EncodeFramePixels) — deliberately not async, so a `Span`/
    /// `ReadOnlySpan` local touching a decoded frame's raw pixels is always
    /// unambiguously safe there regardless of language version; EncodeAsync
    /// itself only ever holds the RETURNED `byte[]` (never a ref struct)
    /// across its own awaits. WITH GPU ENCODE (see below), EncodeFramePixels
    /// still never holds a ref struct across an await ITSELF — the GPU
    /// attempt it may make is a fully blocking call
    /// (`gpuThread.RunAsync(...).GetAwaiter().GetResult()`), not an awaited
    /// one, so this method's own synchronous signature and span-safety
    /// contract are unchanged.
    ///
    /// FORMAT V4 — IndexedDelta7 PIXEL FORMAT: a second EncodeFramePixels
    /// branch (alongside Rgba8888), calling IndexedDelta7Codec.
    ///
    /// FORMAT V5 — Zstd COMPRESSION SCHEME: the per-branch "compress-or-
    /// leave-raw" ternary each of Rgba8888/IndexedDelta7 used to carry
    /// independently is a single shared CompressPlane helper (below) both
    /// branches call through, dispatching on
    /// EditSharpConfig.ScrubProxyCompressionScheme (None/Zstd) in exactly
    /// one place. STRICTLY INTRA-FRAME, LIKE EVERY OTHER PART OF THIS
    /// FORMAT — CompressPlane compresses exactly one frame's own plane in
    /// isolation, with no cross-frame context, preserving the
    /// O(1)-random-access property this whole format exists for.
    ///
    /// FORMAT V6 — IndexedDelta7 SHARED/GLOBAL PALETTE (decided in
    /// conversation, direct response to the user's own real-hardware
    /// measurements of IndexedDelta7+Zstd against a 720p30 size target, and
    /// their explicit choice of a shared/global palette as the next lever —
    /// see ScrubProxyPixelFormat.IndexedDelta7's own SHARED/GLOBAL PALETTE
    /// (V6) remarks for the on-disk-shape reasoning): BuildAsync now runs a
    /// SECOND, SEPARATE, BOUNDED sampling pass before the real encode pass,
    /// ONLY when pixelFormat is IndexedDelta7 — see BuildGlobalDelta7Palette
    /// below. This second pass decodes a small, fixed number of frames
    /// (GlobalPaletteSampleFrameCount) evenly spread across the source's
    /// whole duration (via a SEPARATE SkSourceDecoder.Start call, run at a
    /// low fps chosen so the sample count stays bounded regardless of the
    /// source's real length — a source that's 10 seconds long and one
    /// that's 10 minutes long both cost roughly the same sampling-pass
    /// decode time), accumulates a shared histogram across all of them via
    /// IndexedDelta7Codec.AccumulateHistogram, and median-cuts that into
    /// ONE palette via IndexedDelta7Codec.BuildPaletteFromHistogram — a
    /// genuinely representative, whole-source palette, not a series of
    /// independently-drifting per-frame ones. EncodeAsync then writes that
    /// ONE palette into the file's own new [GlobalPalette] section (see
    /// ScrubProxyFormat's VERSION 6 layout remarks) exactly once, and
    /// EncodeFramePixels's IndexedDelta7 branch calls
    /// IndexedDelta7Codec.EncodeWithPalette against it instead of building
    /// (and storing) a fresh palette per frame — a frame's own on-disk blob
    /// is now JUST its compressed-or-not control-byte plane, no embedded
    /// palette at all. THIS IS A REAL, SECOND, ONE-TIME BUILD-COST DECODE
    /// PASS, NOT FREE — same trade-off family as Zstd's own build-time-only
    /// cost (see ScrubProxyZstd's own remarks): paid once per build, never
    /// per scrub tick, in exchange for removing a per-frame palette's worth
    /// of recurring on-disk size.
    ///
    /// FORMAT V6.1 — GPU ENCODE FOR IndexedDelta7 (direct response to the
    /// user's own explicit request: an earlier round of GPU work — see
    /// ScrubProxyGpuEncoder — sped up READING an already-built proxy; the
    /// actual ask was for the BUILD side to go faster too, since that's
    /// what meaningfully speeds up proxy media generation. NO ON-DISK
    /// FORMAT CHANGE AT ALL — this is purely an alternate, opportunistic
    /// way of computing the exact same bytes EncodeFramePixels's
    /// IndexedDelta7 branch always wrote, so it needed no schema version
    /// bump and no header change): when `hwAccel` is HardwareAccelerator.GPU
    /// and `pixelFormat` is IndexedDelta7, BuildAsync now creates its OWN
    /// GpuContext/SkSurfacePool/GpuThreadDispatcher triple (see GPU ENCODE
    /// CONTEXT LIFETIME below) scoped to this one build, and
    /// EncodeFramePixels tries ScrubProxyGpuEncoder.TryEncode FIRST for
    /// every frame, falling back to the proven IndexedDelta7Codec.
    /// EncodeWithPalette CPU path the moment that GPU attempt declines or
    /// fails for ANY reason — the exact same "opportunistic accelerator,
    /// never the only path" posture this codebase already uses elsewhere.
    /// Rgba8888 builds, and any IndexedDelta7 build running with
    /// HardwareAccelerator.Software, are COMPLETELY UNAFFECTED — this only
    /// ever engages for an IndexedDelta7 build explicitly asked to use the
    /// GPU. THIS IS THE ENCODE SIDE ONLY — there is no corresponding GPU
    /// decode path in this codebase (that earlier work was removed as
    /// unneeded complexity); ScrubProxyReader always reads a frame back on
    /// the CPU, exactly like every other proxy format.
    ///
    /// GPU ENCODE CONTEXT LIFETIME: this build's GPU context is scoped to
    /// ONE BuildAsync call — created just before EncodeAsync runs, disposed
    /// in the same method's own finally block once EncodeAsync returns
    /// (success or failure). A proxy build is a one-shot, already-
    /// expensive, already-logged operation (see the Stopwatch/Log calls
    /// around EncodeAsync below) — there is no "session" for a build-
    /// scoped GPU context to outlive, and creating a fresh D3D12 device/
    /// GRContext per build (rather than trying to share one across
    /// unrelated builds, possibly running for different sources on
    /// different threads) keeps this exactly as safe as this codebase's own
    /// GRContext-thread-confinement contract requires (see
    /// GpuThreadDispatcher's class remarks) without introducing any new
    /// shared, cross-build GPU state to reason about.
    ///
    /// IndexedDelta7 and Zstd are the ONLY pixel format / compression
    /// scheme this cache builds — the earlier Indexed8 pixel format and Rle
    /// compression scheme were both removed (decided in conversation:
    /// unnecessary complexity now that IndexedDelta7+Zstd is the settled
    /// default, best-performing combination).
    /// </summary>
    internal static class ScrubProxyCache
    {
        private const int CurrentSchemaVersion = 1;
        private const string Extension = "esrp";

        /// <summary>
        /// How many frames BuildGlobalDelta7Palette samples, spread
        /// evenly across the WHOLE source, to build IndexedDelta7's one
        /// shared/global palette — see class remarks, FORMAT V6. Chosen as
        /// a fixed, small, bounded count (rather than e.g. "every Nth
        /// frame", which would scale sampling-pass cost with source length)
        /// so a 10-second source and a 10-minute source cost roughly the
        /// same sampling-pass decode time; 32 distinct frames' worth of
        /// color content is already a large multiple of the 128-entry
        /// palette being built from them. NOT VALIDATED AGAINST REAL
        /// CONTENT — same honesty flag as IndexedDelta7Codec's own step-
        /// size constants: a reasonable starting point, not a measured one.
        /// </summary>
        private const int GlobalPaletteSampleFrameCount = 32;

        // See class remarks, IN-FLIGHT BUILD COALESCING HARDENED AGAINST A
        // REAL GetOrAdd RACE — Lazy<Task<T>>, not Task<T> directly, so that
        // a ConcurrentDictionary.GetOrAdd race can only ever construct (and
        // discard) unused Lazy wrappers, never start more than one real
        // build per hash.
        private static readonly ConcurrentDictionary<string, Lazy<Task<ScrubProxyEntry>>> InFlight = new();
        private static readonly ConcurrentDictionary<string, Exception> LastFailure = new();
        private static readonly ConcurrentDictionary<string, ScrubProxyEntry> ResolvedEntries = new();

        private static readonly ConcurrentDictionary<(string Path, long Length, long LastWriteTimeUtcTicks), ScrubProxyEntry>
            SourceResolutionCache = new();

        /// <summary>
        /// Returns a ready-to-use scrub proxy for `sourcePath`, building it
        /// first if no cache entry exists yet — BLOCKS the caller until the
        /// build finishes (or fails). See class remarks, CALLER CONTRACT.
        /// </summary>
        public static async Task<ScrubProxyEntry> GetOrBuildAsync(
            string sourcePath, HardwareAccelerator hwAccel, CancellationToken ct = default)
        {
            ScrubProxyEntry? existing = await TryGetAsync(sourcePath, ct);
            if (existing != null) return existing.Value;

            string hash = await MediaHasher.ComputeAsync(sourcePath, ct);

            Lazy<Task<ScrubProxyEntry>> lazyBuild = InFlight.GetOrAdd(
                hash, _ => new Lazy<Task<ScrubProxyEntry>>(() => BuildAndTrackAsync(sourcePath, hash, hwAccel)));

            return await lazyBuild.Value.WaitAsync(ct);
        }

        /// <summary>
        /// Starts building a scrub proxy for `sourcePath` ahead of need —
        /// mirrors OptimizedMediaCache.PrewarmAsync. Safe to call fire-and-
        /// forget; failures surface later via GetStatusAsync.
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

            if (InFlight.TryGetValue(hash, out Lazy<Task<ScrubProxyEntry>>? lazyBuild))
                return lazyBuild.Value.IsFaulted ? ScrubProxyStatus.Failed : ScrubProxyStatus.Building;

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
        /// size and frame count, optionally runs a bounded sampling pass to
        /// build IndexedDelta7's shared/global palette (see class remarks,
        /// FORMAT V6), decodes+resamples the whole source ONCE for real via
        /// the existing persistent SkSourceDecoder pipe, and writes the
        /// whole self-contained .esrp file via EncodeAsync.
        ///
        /// GPU ENCODE (see class remarks, FORMAT V6.1): when this build is
        /// IndexedDelta7 running with HardwareAccelerator.GPU, a build-
        /// scoped GpuContext/SkSurfacePool/GpuThreadDispatcher triple is
        /// created here, handed down into EncodeAsync, and disposed in this
        /// method's own finally block — see GPU ENCODE CONTEXT LIFETIME.
        /// Any other combination (Rgba8888, or IndexedDelta7 on
        /// HardwareAccelerator.Software) leaves all three null, and
        /// EncodeFramePixels behaves exactly as it always did — CPU only.
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

            int sampleRate = Math.Max(1, (int)Math.Round(EditSharpConfig.ScrubProxySampleRate));
            int frameCount = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds * sampleRate));

            ScrubProxyCompressionScheme compressionScheme = EditSharpConfig.ScrubProxyCompressionScheme;
            ScrubProxyPixelFormat pixelFormat = EditSharpConfig.ScrubProxyPixelFormat;

            DecodeHwAccelPlan plan = await FfmpegRunner.GetDecodePlanAsync(sourcePath, hwAccel);

            // See class remarks, FORMAT V6 — a second, bounded, one-time
            // sampling pass, ONLY for IndexedDelta7, run BEFORE the real
            // encode pass below (which needs the finished palette to
            // encode every frame against).
            byte[] globalPalette = pixelFormat == ScrubProxyPixelFormat.IndexedDelta7
                ? BuildGlobalDelta7Palette(sourcePath, width, height, duration, plan)
                : Array.Empty<byte>();

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

            // See class remarks, FORMAT V6.1 / GPU ENCODE CONTEXT LIFETIME
            // — a build-scoped GPU triple, created only for an
            // IndexedDelta7 build actually asked to use the GPU. All three
            // stay null (EncodeFramePixels's CPU-only behavior is
            // unchanged) for every other combination.
            bool wantGpuEncode = pixelFormat == ScrubProxyPixelFormat.IndexedDelta7 && hwAccel == HardwareAccelerator.GPU;

            GpuContext? gpuContext = null;
            SkSurfacePool? gpuPool = null;
            GpuThreadDispatcher? gpuThread = null;

            if (wantGpuEncode)
            {
                gpuThread = new GpuThreadDispatcher("ScrubProxyGpuEncoder");
                try
                {
                    (gpuContext, gpuPool) = await gpuThread.RunAsync(() =>
                    {
                        GpuContext ctx = GpuContext.Create(hwAccel);
                        SkSurfacePool pool = new(ctx.GRContext, width, height, seedCount: 0);
                        return (ctx, pool);
                    });
                }
                catch (Exception ex)
                {
                    // Creating the GPU context is itself an opportunistic
                    // step — never let a failure here block the build, just
                    // fall back to the CPU-only path exactly as if GPU
                    // encode had never been requested.
                    EditSharpConfig.Logger.LogWarning(
                        "ScrubProxyCache: could not create a GPU context for IndexedDelta7 GPU encode, " +
                        $"building with the CPU path instead: {ex.Message}");
                    gpuThread.Dispose();
                    gpuThread = null;
                    gpuContext = null;
                    gpuPool = null;
                }
            }

            EditSharpConfig.Logger.Log(
                $"Building scrub proxy for '{sourcePath}' ({width}x{height} @ {sampleRate}/s, {frameCount} " +
                $"frames, pixelFormat={pixelFormat}, compression={compressionScheme}" +
                (globalPalette.Length > 0 ? ", shared palette built from a sample pass" : "") +
                (gpuContext?.GRContext != null ? ", GPU encode enabled" : "") + ")...");
            var sw = Stopwatch.StartNew();

            try
            {
                try
                {
                    await EncodeAsync(
                        sourcePath, tempPath, width, height, sampleRate, frameCount, pixelFormat, compressionScheme,
                        globalPalette, meta, plan, gpuContext, gpuPool, gpuThread);
                    File.Move(tempPath, finalPath, overwrite: true);
                }
                catch
                {
                    TryDelete(tempPath);
                    throw;
                }
            }
            finally
            {
                // See class remarks, GPU ENCODE CONTEXT LIFETIME — this
                // build's own GPU objects are torn down here, unconditionally,
                // whether EncodeAsync succeeded or threw. Disposal itself is
                // marshaled onto the same dedicated thread that created and
                // used them (GpuContext.Dispose touches the GRContext, and
                // must therefore run on the thread that owns it, same as
                // every other GRContext-touching call).
                if (gpuThread != null)
                {
                    try
                    {
                        await gpuThread.RunAsync(() =>
                        {
                            gpuPool?.Dispose();
                            gpuContext?.Dispose();
                        });
                    }
                    catch (Exception ex)
                    {
                        EditSharpConfig.Logger.LogVerbose(
                            $"ScrubProxyCache: GPU encode context teardown threw: {ex.Message}");
                    }
                    finally
                    {
                        gpuThread.Dispose();
                    }
                }
            }

            EditSharpConfig.Logger.Log(
                $"Scrub proxy built in {sw.ElapsedMilliseconds}ms -> {finalPath}");

            return new ScrubProxyEntry(finalPath, width, height, sampleRate, frameCount);
        }

        /// <summary>
        /// See class remarks, FORMAT V6. Decodes GlobalPaletteSampleFrameCount
        /// frames, evenly spread across `duration`, via a SEPARATE
        /// SkSourceDecoder.Start call (its own persistent pipe, distinct
        /// from the real encode pass's own decoder — this class already
        /// establishes the pattern of one decoder instance per linear pass
        /// elsewhere), accumulates their color histogram, and median-cuts
        /// it into one 384-byte shared IndexedDelta7 palette. The sampling
        /// fps is chosen so the TOTAL number of frames actually decoded
        /// stays close to GlobalPaletteSampleFrameCount regardless of
        /// `duration` — a source shorter than that many seconds at 1fps
        /// simply samples every second of it instead (clamped so the
        /// sampling decoder is never asked for an fps below 1). This pass
        /// stays CPU-only even for a GPU-encode build — see class remarks,
        /// FORMAT V6.1: it touches a small, fixed number of frames
        /// (GlobalPaletteSampleFrameCount, not the whole source), so it was
        /// never the actual cost GPU encode is aimed at.
        /// </summary>
        private static byte[] BuildGlobalDelta7Palette(
            string sourcePath, int width, int height, TimeSpan duration, DecodeHwAccelPlan plan)
        {
            double targetSampleFps = GlobalPaletteSampleFrameCount / Math.Max(duration.TotalSeconds, 1.0);
            int sampleFps = Math.Max(1, (int)Math.Round(Math.Min(targetSampleFps, GlobalPaletteSampleFrameCount)));

            var histogram = new Dictionary<uint, int>();

            using (SkSourceDecoder sampleDecoder = SkSourceDecoder.Start(
                sourcePath, 0, sampleFps, width, height, plan, fastOpen: false))
            {
                int samplesToTake = Math.Max(1, GlobalPaletteSampleFrameCount);
                for (int i = 0; i < samplesToTake; i++)
                {
                    using SKImage sampleFrame = sampleDecoder.NextFrame();
                    using SKPixmap? pixmap = sampleFrame.PeekPixels();
                    if (pixmap == null) continue;

                    IndexedDelta7Codec.AccumulateHistogram(pixmap.GetPixelSpan(), width, height, histogram);
                }
            }

            byte[] palette = new byte[ScrubProxyFormat.Delta7PaletteByteSize];
            IndexedDelta7Codec.BuildPaletteFromHistogram(histogram, palette);
            return palette;
        }

        /// <summary>
        /// Decodes `sourcePath` ONCE, resampled to `fps`/`width`x`height`,
        /// and writes the complete self-contained .esrp file to
        /// `outputPath` — fixed header, then the embedded metadata blob,
        /// then the shared-palette section (see ScrubProxyFormat's VERSION
        /// 6 layout remarks — zero-length when `globalPalette` is empty),
        /// then a placeholder frame-index region, then every frame's stored
        /// bytes back to back, then a seek-back to fill in the frame index.
        ///
        /// `gpuContext`/`gpuPool`/`gpuThread` are this build's own GPU
        /// encode triple (see class remarks, FORMAT V6.1) — all null for
        /// every build that isn't IndexedDelta7-on-GPU. Handed straight
        /// through to EncodeFramePixels, unchanged, for every frame; this
        /// method itself has no GPU-vs-CPU branching of its own.
        /// </summary>
        private static async Task EncodeAsync(
            string sourcePath, string outputPath, int width, int height, int fps, int frameCount,
            ScrubProxyPixelFormat pixelFormat, ScrubProxyCompressionScheme compressionScheme,
            byte[] globalPalette, ScrubProxyMeta meta, DecodeHwAccelPlan plan,
            GpuContext? gpuContext, SkSurfacePool? gpuPool, GpuThreadDispatcher? gpuThread)
        {
            using SkSourceDecoder decoder = SkSourceDecoder.Start(
                sourcePath, 0, fps, width, height, plan, fastOpen: false);

            byte[] metaBytes = ScrubProxyMetaSerializer.SerializeToUtf8Bytes(meta);

            long globalPaletteOffset = ScrubProxyFormat.GlobalPaletteOffset(metaBytes.Length);
            long frameIndexOffset = ScrubProxyFormat.FrameIndexOffset(metaBytes.Length, globalPalette.Length);
            int frameIndexByteSize = frameCount * ScrubProxyFormat.FrameIndexEntrySize;
            long frameDataStartOffset =
                ScrubProxyFormat.FrameDataStartOffset(metaBytes.Length, globalPalette.Length, frameCount);

            using var stream = new FileStream(
                outputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20, useAsync: true);

            byte[] header = new byte[ScrubProxyFormat.HeaderSize];
            ScrubProxyFormat.WriteHeader(
                header, width, height, pixelFormat, compressionScheme, fps, frameCount, metaBytes.Length,
                globalPalette.Length);
            await stream.WriteAsync(header);

            await stream.WriteAsync(metaBytes);

            if (globalPalette.Length > 0)
                await stream.WriteAsync(globalPalette);

            // Reserve the frame index region with zeros for now — every
            // entry's real (offset, length) is only known once its frame
            // has actually been written below.
            await stream.WriteAsync(new byte[frameIndexByteSize]);

            var frameIndex = new (long Offset, int Length)[frameCount];
            long cursor = frameDataStartOffset;

            for (int i = 0; i < frameCount; i++)
            {
                using SKImage frame = decoder.NextFrame();

                byte[] stored = EncodeFramePixels(
                    frame, sourcePath, i, width, height, pixelFormat, compressionScheme, globalPalette,
                    gpuContext, gpuPool, gpuThread);

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

            // globalPaletteOffset is unused past this point (the palette
            // was already written in file order above) — kept as a local
            // purely to name/document the section's start alongside the
            // others for anyone reading this method.
            _ = globalPaletteOffset;
        }

        /// <summary>
        /// PLAIN, FULLY SYNCHRONOUS — see class remarks. Reads one decoded
        /// frame's raw pixels via SKPixmap.GetPixelSpan() and returns this
        /// frame's complete on-disk blob, shaped according to `pixelFormat`:
        ///   - Rgba8888: CompressPlane's result on the raw bytes.
        ///   - IndexedDelta7 (see class remarks, FORMAT V6): `globalPalette`
        ///     (built once, before this method is ever called — see
        ///     BuildGlobalDelta7Palette) is handed to
        ///     IndexedDelta7Codec.EncodeWithPalette instead of this method
        ///     building/writing a fresh per-frame palette. The returned
        ///     blob is JUST the compressed-or-not control-byte plane — no
        ///     palette bytes in it at all any more.
        ///
        /// GPU ENCODE (see class remarks, FORMAT V6.1): the IndexedDelta7
        /// branch now tries ScrubProxyGpuEncoder.TryEncode FIRST whenever
        /// `gpuContext`/`gpuPool`/`gpuThread` are all non-null (they're
        /// either all null or all non-null together — see BuildAsync),
        /// dispatched via `gpuThread.RunAsync(...).GetAwaiter().GetResult()`
        /// so this method's own synchronous signature (see class remarks,
        /// FRAME-PIXEL ENCODING IS A PLAIN SYNCHRONOUS HELPER) never
        /// changes shape — EncodeAsync's own await loop still just calls
        /// this like an ordinary synchronous method, and the blocking wait
        /// here is exactly the point: this frame's bytes must be finished,
        /// one way or the other, before the loop's next stream.WriteAsync.
        /// On ANY GPU failure (TryEncode returning false, or the dispatched
        /// call itself throwing), this falls straight through to the same
        /// IndexedDelta7Codec.EncodeWithPalette call this method always
        /// made — see ScrubProxyGpuEncoder's own remarks on why that's
        /// always safe to do unconditionally.
        /// </summary>
        private static byte[] EncodeFramePixels(
            SKImage frame, string sourcePath, int frameIndex, int width, int height,
            ScrubProxyPixelFormat pixelFormat, ScrubProxyCompressionScheme compressionScheme,
            byte[] globalPalette,
            GpuContext? gpuContext, SkSurfacePool? gpuPool, GpuThreadDispatcher? gpuThread)
        {
            using SKPixmap? pixmap = frame.PeekPixels();
            if (pixmap == null)
                throw new InvalidOperationException(
                    $"Could not read decoded pixels while building a scrub proxy for '{sourcePath}' (frame {frameIndex}).");

            ReadOnlySpan<byte> raw = pixmap.GetPixelSpan();

            if (pixelFormat == ScrubProxyPixelFormat.IndexedDelta7)
            {
                byte[] pixelCodes = new byte[width * height];

                bool gpuHandled = false;
                if (gpuContext != null && gpuPool != null && gpuThread != null)
                {
                    try
                    {
                        // See method remarks, GPU ENCODE — a deliberate
                        // blocking wait, not an await: this method's own
                        // signature must stay synchronous (see class
                        // remarks, FRAME-PIXEL ENCODING IS A PLAIN
                        // SYNCHRONOUS HELPER), and EncodeAsync's caller-side
                        // loop needs this frame's bytes fully resolved
                        // before it can proceed to the next one regardless.
                        gpuHandled = gpuThread.RunAsync(() =>
                            ScrubProxyGpuEncoder.TryEncode(
                                gpuContext, gpuPool, frame, globalPalette, width, height, pixelCodes))
                            .GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        // Same posture as ScrubProxyGpuEncoder.TryEncode's
                        // own internal try/catch — this outer one exists
                        // because the dispatched RunAsync call itself can
                        // fault (e.g. the dedicated GPU thread has already
                        // torn down), which is a different failure surface
                        // than TryEncode returning false for a reason it
                        // caught internally.
                        EditSharpConfig.Logger.LogWarning(
                            "ScrubProxyCache: GPU IndexedDelta7 encode dispatch failed for frame " +
                            $"{frameIndex} of '{sourcePath}', falling back to the CPU path: {ex.Message}");
                        gpuHandled = false;
                    }
                }

                if (!gpuHandled)
                    IndexedDelta7Codec.EncodeWithPalette(raw, width, height, globalPalette, pixelCodes);

                return CompressPlane(pixelCodes, compressionScheme);
            }

            return CompressPlane(raw, compressionScheme);
        }

        /// <summary>
        /// SHARED None/Zstd DISPATCH — see class remarks, FORMAT V5. Every
        /// place this cache compresses one frame's own byte plane goes
        /// through here instead of its own inline ternary.
        /// </summary>
        private static byte[] CompressPlane(ReadOnlySpan<byte> plane, ScrubProxyCompressionScheme compressionScheme) =>
            compressionScheme switch
            {
                ScrubProxyCompressionScheme.None => plane.ToArray(),
                ScrubProxyCompressionScheme.Zstd => ScrubProxyZstd.Encode(plane),
                _ => throw new InvalidOperationException(
                    $"ScrubProxyCache: unknown ScrubProxyCompressionScheme '{compressionScheme}'."),
            };

        /// <summary>
        /// Looks up an existing entry on disk by hash — the read side of
        /// the cache. Fully synchronous — reading the header/metadata is a
        /// couple of small positioned reads.
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
        /// the source's own SHORT side, the other axis scaled
        /// proportionally, aspect preserved, never upscaled past native.
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
        /// One self-contained .esrp file per entry (see class remarks,
        /// FORMAT V2).
        /// </summary>
        private static (string MediaPath, string Directory) PathFor(string hash)
        {
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