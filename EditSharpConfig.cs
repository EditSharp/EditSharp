using System;
using EditSharp.Composite;

namespace EditSharp
{
    /// <summary>
    /// Process-wide configuration for EditSharp. Set these once at startup,
    /// before the first call into TimelineAssembler — nothing here is
    /// per-render, so a value changed mid-flight applies to every render
    /// sharing the process from that point on, not just the next one.
    /// </summary>
    public static class EditSharpConfig
    {
        private static string _tempDirectory = Path.Combine(AppContext.BaseDirectory, "Temp");

        /// <summary>
        /// Root directory EditSharp writes scratch files into — rasterized text
        /// PNGs, oversized filter-graph scripts, segmented-render intermediates.
        ///
        /// Defaults to AppContext.BaseDirectory (next to the host app's own
        /// executable), NOT the OS temp path — do not change this default to
        /// Path.GetTempPath(). Testing traced a native ffmpeg crash (0xc0000409,
        /// a stack buffer overrun) specifically to scratch files living under
        /// Windows %TEMP%, most likely because that path's length (deep under
        /// AppData\Local\Temp, plus a GUID filename) trips a bug in the
        /// comparatively less-mature "-/option" file-loading mechanism ffmpeg
        /// uses for large filter graphs. A consumer is free to point
        /// TempDirectory at %TEMP% anyway, but it's an opt-in regression, not
        /// the shipped default.
        ///
        /// EditSharp creates its own subfolders under this directory lazily, on
        /// first write — the host app does not need to pre-create anything, and
        /// setting this to a path that doesn't exist yet at all is fine too.
        /// </summary>
        public static string TempDirectory
        {
            get => _tempDirectory;
            set => _tempDirectory = value ?? throw new ArgumentNullException(nameof(value));
        }

        private static string _optimizedMediaDirectory = Path.Combine(AppContext.BaseDirectory, "OptimizedMedia");

        /// <summary>
        /// Root directory OptimizedMediaCache reads and writes its
        /// persistent, content-addressed optimized media into — the
        /// DNxHR/ProRes proxies (plus their .meta.json companions and the
        /// hash-index.json sidecar MediaHasher maintains) that back fast
        /// seeking during playback and, opportunistically, rendering.
        ///
        /// DEFAULT MATCHES TempDirectory'S OWN CONVENTION, DECIDED IN
        /// CONVERSATION: AppContext.BaseDirectory (next to the host app's
        /// own executable), not the OS temp path or app-data — this is
        /// UNLIKE TempDirectory in one important way, though: this
        /// directory's contents are meant to PERSIST across app runs
        /// (that's the entire point of a content-addressed cache an NLE
        /// can reopen a project against later — see OptimizedMediaCache's
        /// own class remarks), so nothing in this pipeline ever deletes
        /// from here the way Renderer/Playback sweep their own tempFiles
        /// bags. A consumer that wants this cache cleared is expected to
        /// delete the directory itself (or point this at a fresh one).
        ///
        /// Created lazily on first write, same as TempDirectory — nothing
        /// needs to exist here ahead of time.
        /// </summary>
        public static string OptimizedMediaDirectory
        {
            get => _optimizedMediaDirectory;
            set => _optimizedMediaDirectory = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// Which codec OptimizedMediaCache builds persistent optimized media
        /// with — DNxHR or ProRes, decided in conversation to be
        /// configurable rather than fixed to one. Defaults to
        /// VideoCodec.DNxHR: an open format with no licensing friction,
        /// where ffmpeg's encoder support is equally solid cross-platform —
        /// see VideoCodec.DNxHR's own remarks for the fuller reasoning.
        ///
        /// CHANGING THIS MID-PROJECT DOES NOT INVALIDATE EXISTING CACHE
        /// ENTRIES built under the previous codec — they simply stop being
        /// matched by TryLoadExistingAsync (which checks the configured
        /// codec against each entry's own recorded one) and are
        /// transparently rebuilt, at the SAME hash-addressed path, the next
        /// time something asks for that source's optimized media. See
        /// OptimizedMediaCache.TryLoadExistingAsync's own remarks on why
        /// DNxHR and ProRes sharing the ".mov" extension is deliberate, not
        /// a collision to avoid.
        /// </summary>
        public static VideoCodec OptimizedMediaCodec { get; set; } = VideoCodec.DNxHR;

        private static int _optimizedMediaMaxDimension = 3840;

        /// <summary>
        /// The cap OptimizedMediaCache builds a source's optimized media
        /// at, on its longest axis — native resolution if the source is
        /// already at or under this, otherwise downscaled (aspect
        /// preserved, never upscaled) to fit it. Defaults to 3840 (a 4K
        /// cap) — decided in conversation as "one canonical resolution per
        /// source, capped at a configurable max" rather than multiple
        /// quality tiers; see OptimizedMediaCache's class remarks for the
        /// full reasoning, including what happens when a clip actually
        /// needs MORE resolution than this cap provides (falls back to the
        /// true original source rather than upscaling from the proxy — see
        /// RenderContentPreparation.ProbeVideoAsync).
        /// </summary>
        public static int OptimizedMediaMaxDimension
        {
            get => _optimizedMediaMaxDimension;
            set => _optimizedMediaMaxDimension = value > 0
                ? value
                : throw new ArgumentOutOfRangeException(
                    nameof(value), "OptimizedMediaMaxDimension must be positive.");
        }

        private static string _scrubProxyDirectory = Path.Combine(AppContext.BaseDirectory, "ScrubProxy");

        /// <summary>
        /// Root directory ScrubProxyCache reads and writes its persistent,
        /// content-addressed raw scrub proxies into — self-contained .esrp
        /// files (metadata embedded, no separate companion file — see
        /// ScrubProxyFormat/ScrubProxyMeta/ScrubProxyCache). A DELIBERATELY
        /// SEPARATE directory from OptimizedMediaDirectory — see
        /// ScrubProxyCache's own class remarks on why this is a distinct
        /// cache, not a third quality tier of optimized media.
        ///
        /// Same persistence contract as OptimizedMediaDirectory: nothing in
        /// this pipeline ever deletes from here on its own; a consumer that
        /// wants it cleared deletes the directory (or points this at a
        /// fresh one). Created lazily on first write.
        /// </summary>
        public static string ScrubProxyDirectory
        {
            get => _scrubProxyDirectory;
            set => _scrubProxyDirectory = value ?? throw new ArgumentNullException(nameof(value));
        }

        private static int _scrubProxyTargetShortSide = 216;

        /// <summary>
        /// The target size, on whichever axis is a source's own SHORT side,
        /// that ScrubProxyCache builds a scrub proxy at (the other axis
        /// scaled proportionally, aspect preserved, never upscaled past
        /// native — see ScrubProxyCache.ComputeProxySize). Defaults to 144
        /// ("144p"-scale) — decided in conversation: small enough that
        /// decode/scale cost during the one-time build is trivial and the
        /// resulting .esrp file stays a reasonable size, while still being
        /// a perfectly legible scrub preview at typical preview-window
        /// sizes (this is a scrub/rewind indicator, never used for a final
        /// render — see ScrubFrameSource).
        ///
        /// WORTH REVISITING NOW THAT Indexed8 EXISTS (see
        /// ScrubProxyPixelFormat.Indexed8's own remarks): the whole point of
        /// palette quantization's size savings is that they buy back room
        /// to raise this toward native resolution. Left at its original
        /// default here rather than changed as part of this round — nothing
        /// about Indexed8's own correctness depends on a particular target
        /// short side, so raising this is a separate, purely-quality-vs-
        /// size tuning decision for later, not bundled into the format
        /// change itself.
        /// </summary>
        public static int ScrubProxyTargetShortSide
        {
            get => _scrubProxyTargetShortSide;
            set => _scrubProxyTargetShortSide = value > 0
                ? value
                : throw new ArgumentOutOfRangeException(
                    nameof(value), "ScrubProxyTargetShortSide must be positive.");
        }

        private static double _scrubProxySampleRate = 30.0;

        /// <summary>
        /// How many frames per second of SOURCE TIME a scrub proxy stores —
        /// completely decoupled from the source's own fps or keyframe
        /// spacing (see ScrubProxyFormat's own remarks). Defaults to 10.0:
        /// finer scrub granularity than the earlier keyframe-snapped
        /// approach ever gave (keyframes can be several seconds apart),
        /// while keeping a proxy's file size and one-time build cost
        /// reasonable. Raising this trades disk space and build time for
        /// finer scrub granularity; a fast human drag rarely perceives
        /// granularity finer than this by much.
        ///
        /// Rounded to the nearest integer at build time (see
        /// ScrubProxyCache.BuildAsync) — the stored header value and the
        /// decoder's own fps-conform target must be the exact same number,
        /// or GetFrameAt's seek arithmetic would desync from what was
        /// actually decoded.
        /// </summary>
        public static double ScrubProxySampleRate
        {
            get => _scrubProxySampleRate;
            set => _scrubProxySampleRate = value > 0
                ? value
                : throw new ArgumentOutOfRangeException(
                    nameof(value), "ScrubProxySampleRate must be positive.");
        }

        private static ScrubProxyCompressionScheme _scrubProxyCompressionScheme = ScrubProxyCompressionScheme.Rle;

        /// <summary>
        /// Which lossless per-frame transform, if any, ScrubProxyCache
        /// applies to every stored frame in a NEWLY BUILT .esrp scrub proxy
        /// — see ScrubProxyFormat.ScrubProxyCompressionScheme and
        /// ScrubProxyRle for the actual codec. Defaults to Rle: confirmed,
        /// via real-world testing in a separate application, to cost
        /// negligible CPU even on a hot per-tick decode path, for a real,
        /// often substantial reduction in a scrub proxy's on-disk size —
        /// flat colour, letterboxing/pillarboxing, and gradient-heavy
        /// footage in particular compress well. Set to None to build fully
        /// raw proxies instead (the original v1 shape, still supported —
        /// just no longer the default).
        ///
        /// ONLY AFFECTS NEW BUILDS. An existing cached .esrp file's own
        /// CompressionScheme (recorded in its own header at build time) is
        /// what ScrubProxyReader actually honors when reading it back —
        /// changing this setting does not retroactively touch anything
        /// already on disk, and there is no need to rebuild existing
        /// entries just because this changed; old and new entries coexist
        /// fine side by side in the same cache directory. APPLIES TO
        /// Indexed8 FRAMES' INDEX-BYTE PLANE TOO (see
        /// ScrubProxyPixelFormat.Indexed8's own remarks) — this one knob
        /// governs both pixel formats' own compressible stream, whichever
        /// ScrubProxyPixelFormat below is also configured.
        /// </summary>
        public static ScrubProxyCompressionScheme ScrubProxyCompressionScheme
        {
            get => _scrubProxyCompressionScheme;
            set => _scrubProxyCompressionScheme = value;
        }

        private static ScrubProxyPixelFormat _scrubProxyPixelFormat = ScrubProxyPixelFormat.Indexed8;

        /// <summary>
        /// How ScrubProxyCache stores each frame's pixels in a NEWLY BUILT
        /// .esrp scrub proxy — see ScrubProxyFormat.ScrubProxyPixelFormat
        /// and ColorQuantizer for the actual quantization/dithering
        /// machinery Indexed8 relies on. DEFAULTS TO Indexed8 — DECIDED IN
        /// CONVERSATION: this whole format was added specifically because
        /// its size savings are large enough to be worth taking as the
        /// default trade-off for a scrub PREVIEW (never used for a final
        /// render — see ScrubProxyFormat's own class remarks on the
        /// "accurate to the proxy, not the source" trade-off this whole
        /// mechanism already makes regardless of pixel format). Set to
        /// Rgba8888 to build fully lossless (modulo CompressionScheme)
        /// proxies instead — the original v1/v2 shape, still fully
        /// supported, just no longer the default now that Indexed8 exists.
        ///
        /// ONLY AFFECTS NEW BUILDS — same non-retroactive contract as
        /// ScrubProxyCompressionScheme immediately above: an existing
        /// cached .esrp file's own PixelFormat (recorded in its own header
        /// at build time) is what ScrubProxyReader actually honors when
        /// reading it back, so changing this does not touch anything
        /// already on disk, and Rgba8888/Indexed8 entries coexist fine
        /// side by side in the same cache directory.
        /// </summary>
        public static ScrubProxyPixelFormat ScrubProxyPixelFormat
        {
            get => _scrubProxyPixelFormat;
            set => _scrubProxyPixelFormat = value;
        }

        /// <summary>
        /// Where EditSharp reports render progress and diagnostics. Defaults to
        /// a no-op logger, so a consumer who never sets this gets silence rather
        /// than an exception or unexpected console output.
        /// </summary>
        public static IEditSharpLogger Logger { get; set; } = NullEditSharpLogger.Instance;

        /// <summary>
        /// Executable name or full path used to launch ffmpeg. Defaults to
        /// "ffmpeg", resolved from PATH.
        /// </summary>
        public static string FfmpegPath { get; set; } = "ffmpeg";

        /// <summary>
        /// Executable name or full path used to launch ffprobe. Defaults to
        /// "ffprobe", resolved from PATH.
        /// </summary>
        public static string FfprobePath { get; set; } = "ffprobe";

        private static int _filterThreads = 1;

        /// <summary>
        /// How many threads libavfilter may use to execute a filter graph
        /// (ffmpeg's -filter_threads / -filter_complex_threads). Defaults to
        /// 1 — deliberately NOT the processor count ffmpeg would otherwise
        /// pick on its own.
        ///
        /// THIS DEFAULT IS A CORRECTNESS FIX, NOT A TUNING CHOICE.
        ///
        /// Rendering on a 16-logical-core machine produced exactly 15 evenly
        /// spaced horizontal black lines across every frame — 15 being
        /// precisely the number of INTERIOR boundaries between 16 slices.
        /// Measured off a real affected frame: the lines sat at rows 60, 129,
        /// 198 ... 1026 (a dead-uniform 69px pitch, 15 of them), and spanned
        /// x=960 to x=1919 — exactly the right half of a 1920-wide canvas, to
        /// the pixel. Both numbers are structural rather than incidental: a
        /// count equal to threads-1, and a boundary at exactly width/2, is
        /// what a per-thread work partition looks like when the seams between
        /// its regions don't reconstruct cleanly. The artifact is a
        /// one-pixel-tall row of ZEROED ALPHA at each seam, which then lets
        /// the opaque black backdrop FrameFilterChain.Flatten composites onto
        /// show through — hence "black lines" rather than discoloured ones.
        ///
        /// Three independent observations ruled out every non-threading
        /// explanation that was considered:
        ///   - The lines DON'T move or change count when the canvas
        ///     resolution changes. A frame-size-derived partition (an FFV1
        ///     slice grid, a buffer-size seam) necessarily would; a
        ///     thread-count-derived one doesn't, because thread count is a
        ///     property of the machine and not of the frame.
        ///   - They appear with NO NoiseClip present, and on a blueprint of a
        ///     single GeneratorClip with no effects on a Normal-blend channel
        ///     — which rules out perlin, FFV1 (that graph decodes nothing at
        ///     all; its content is a `color` source), and every effect chain.
        ///   - They are already in the RAW ACCUMULATOR, before
        ///     FinalizeOutputAsync's H.264/yuv420p encode ever runs, so the
        ///     final encode is not introducing them either.
        ///
        /// What's left is the per-frame filter graph itself, executed
        /// multi-threaded. Every clip runs `perspective` unconditionally
        /// (ClipVideoChain.Build always calls ApplyTransform, identity
        /// transform or not), `scale` several times including the
        /// MaskSupersample up/down pair, and the accumulator's own starting
        /// frame (GraphUtilities.BuildTransparentBlank) is built fresh on
        /// every single render regardless of blueprint — all filters
        /// libavfilter is free to slice across threads. Worth knowing for
        /// anyone revisiting this: ffmpeg 8.0 (this project's target) ships a
        /// rewritten, multi-threaded swscale, so a `scale` that was
        /// effectively serial on the ffmpeg this pipeline was first written
        /// against is not serial any more.
        ///
        /// A PER-FILTER version of this fix (a `threads=1` pin on only the
        /// specific filters implicated, leaving the rest of the graph free to
        /// thread) was built, tested, and reverted — worth recording why, since
        /// it looked like the right call at the time. An early A/B seemed to
        /// show the global pin costing roughly 3x per-frame render time on a
        /// real blueprint (4-7s baseline vs 15s+ pinned), which justified the
        /// extra complexity. That comparison turned out to be invalid: the
        /// baseline was measured at Blueprint.FrameRenderConcurrency=1 and the
        /// pinned case at FrameRenderConcurrency=4 — an unrelated variable
        /// left inconsistent between the two runs, not a cost of the pin
        /// itself. Re-measured at matched concurrency, the global pin's actual
        /// cost was negligible on this blueprint and every other one tested.
        /// The per-filter mechanism (GraphUtilities.ThreadPin and a `threads=1`
        /// suffix threaded through every filter emission in ClipVideoChain and
        /// FrameFilterChain) was real code paid for under a false premise, so
        /// it was removed rather than kept "just in case" — simplicity won
        /// once the actual numbers were in. If a genuine need to reclaim
        /// filter-level parallelism ever shows up, EditSharp-Handoff.md /
        /// project history has the reverted version to start from rather than
        /// rebuilding it from scratch.
        ///
        /// The single-frame render design is what makes 1 an acceptable
        /// default rather than a painful one: parallelism here is better
        /// spent at the PROCESS level (Blueprint.FrameRenderConcurrency,
        /// which renders whole frames concurrently) than inside one frame's
        /// graph, since whole frames are perfectly independent while filter
        /// slices have to stitch back together — which is the very thing
        /// going wrong. If you raise this, raise it while watching for the
        /// lines to come back.
        /// </summary>
        public static int FilterThreads
        {
            get => _filterThreads;
            set => _filterThreads = value >= 1
                ? value
                : throw new ArgumentOutOfRangeException(
                    nameof(value), "FilterThreads must be at least 1.");
        }

        /// <summary>
        /// Which swscale backend(s) ffmpeg 9.0+ is allowed to use for scale/
        /// format conversions (the scaler's -sws_backends flag). Defaults to
        /// "x86" — explicitly opting in to the new x86 SIMD kernel backend
        /// from 9.0's swscale rewrite.
        ///
        /// THIS DEFAULT IS DELIBERATE, NOT FFMPEG'S OWN DEFAULT. Per FFmpeg's
        /// scaler docs, sws_backends defaults to 'auto', which resolves to
        /// 'stable' unless the 'unstable' flag is set — and SWS_BACKEND_X86
        /// is defined as part of SWS_BACKEND_UNSTABLE, not
        /// SWS_BACKEND_STABLE. So on a stock 9.0 build, every scale/format
        /// conversion in this pipeline is running the plain C reference
        /// path, not the SIMD path, unless this is set explicitly.
        ///
        /// This is orthogonal to FilterThreads, not a rerun of the same
        /// bug: FilterThreads controls libavfilter's cross-thread SLICING of
        /// a frame into pieces (the thing that produced the black-line
        /// seams — see that property's remarks), while sws_backends
        /// controls which SIMD kernel swscale uses to do the per-pixel math
        /// WITHIN a single thread's execution. FilterThreads=1 means there
        /// is only one slice/one thread; sws_backends=x86 just makes that
        /// one thread faster. Nothing here re-partitions the frame, so the
        /// seam mechanism the black-line fix addressed shouldn't apply.
        ///
        /// That said, this has not yet been verified against a real render
        /// on the actual 9.0 build — ffmpeg's own docs describe x86 as an
        /// "unstable" backend (their word, likely meaning "newer/less
        /// battle-tested", not literally unsafe), and the swscale rewrite
        /// itself is very new. If a render regresses in a way that smells
        /// at all like the black-line bug (seams, banding, wrong alpha)
        /// after this is turned on, that is the first thing to revert and
        /// would mean the two are not as orthogonal in practice as they are
        /// on paper.
        /// </summary>
        public static string SwsBackends { get; set; } = "x86";
    }

    /// <summary>
    /// Minimal logging seam so EditSharp doesn't assume Console, a specific
    /// logging framework, or any other ambient logger exists in the host
    /// process. Adapt to Microsoft.Extensions.Logging, Serilog, etc. with a
    /// one-line wrapper — e.g. <c>msg => _logger.LogInformation(msg)</c>.
    /// </summary>
    public interface IEditSharpLogger
    {
        //standard log for information that is important to an end user
        //(ex. "render complete in x seconds", "
        void Log(string message);

        //log for debug information and information about each individual step of a process
        //(ex. info on renders of individual frames)
        void LogVerbose(string message);

        //log for when an issue occurs, but the issue is not fatal
        //(ex. fallbacks during a GPU render)
        void LogWarning(string message);

        //log for when an unrecoverable issue occurs
        //(ex. a throw happens during a render or ffmpeg crashes during a render)
        void LogError(string message);
    }

    internal sealed class NullEditSharpLogger : IEditSharpLogger
    {
        public static readonly NullEditSharpLogger Instance = new();
        private NullEditSharpLogger() { }
        public void Log(string message) { }
        public void LogVerbose(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message) { }
    }
}