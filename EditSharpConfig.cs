using System;
using EditSharp.Caching.Proxy;
using EditSharp.Video;

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

        private static string _proxyDirectory = Path.Combine(AppContext.BaseDirectory, "Proxy");

        /// <summary>
        /// Root directory ProxyCache reads and writes proxies in (and where
        /// MediaHasher keeps its hash index). Persistent across runs: nothing
        /// in EditSharp ever deletes it, so a consumer can point it somewhere
        /// durable and reopen projects against the same proxies later.
        /// </summary>
        public static string ProxyDirectory
        {
            get => _proxyDirectory;
            set => _proxyDirectory = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// The format ProxyCache.BuildAsync builds when none is named. The
        /// .esrp formats read with no subprocess at all (true random access);
        /// DNxHR/ProRes are for compatibility and read through ffmpeg.
        /// </summary>
        public static ProxyFormat ProxyFormat { get; set; } = ProxyFormat.EsrpDelta7;

        private static int _proxyMaxDimension = 1280;

        /// <summary>
        /// The longest side a proxy is built at (never upscaled). Proxies keep
        /// the source's own frame rate, so one proxy serves scrubbing and
        /// playback alike.
        /// </summary>
        public static int ProxyMaxDimension
        {
            get => _proxyMaxDimension;
            set => _proxyMaxDimension = value > 0
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), "ProxyMaxDimension must be positive.");
        }

        /// <summary>How .esrp proxies compress each frame. Applies to newly built proxies only.</summary>
        public static EsrpCompressionScheme EsrpCompressionScheme { get; set; } = EsrpCompressionScheme.Zstd;

        private static int _esrpCompressionLevel = 9;

        /// <summary>
        /// Zstd level for .esrp proxy frames (1-22). Higher is smaller and
        /// slower to build: on 1280x720 frames, 9 is about 18x faster than 19
        /// for about 10% more disk. Reading is the same speed at any level.
        /// </summary>
        public static int EsrpCompressionLevel
        {
            get => _esrpCompressionLevel;
            set => _esrpCompressionLevel = value is >= 1 and <= 22
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), "EsrpCompressionLevel must be between 1 and 22.");
        }

        private static int _maxConcurrentProxyBuilds = 1;

        /// <summary>
        /// How many proxies may build at once; further BuildAsync calls queue
        /// (ProxyState.Queued) in order. Read when a build is dequeued, so a
        /// change applies to builds that haven't started yet.
        /// </summary>
        public static int MaxConcurrentProxyBuilds
        {
            get => _maxConcurrentProxyBuilds;
            set => _maxConcurrentProxyBuilds = value >= 1
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), "MaxConcurrentProxyBuilds must be at least 1.");
        }

        private static TimeSpan _sourceRetryInterval = TimeSpan.FromSeconds(2);

        /// <summary>
        /// How long a preview waits before retrying a source that failed with
        /// MediaOffline or DecodeError (a drive may be reconnected, a file
        /// re-exported). Renders never retry.
        /// </summary>
        public static TimeSpan SourceRetryInterval
        {
            get => _sourceRetryInterval;
            set => _sourceRetryInterval = value > TimeSpan.Zero
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), "SourceRetryInterval must be positive.");
        }

        private static TimeSpan _sourceLookahead = TimeSpan.FromSeconds(2);

        /// <summary>
        /// How far ahead of the playhead (behind, in reverse) playback and
        /// export prepare a clip's sources and open their readers, so a clip
        /// arriving on screen already has frames waiting instead of stalling.
        /// </summary>
        public static TimeSpan SourceLookahead
        {
            get => _sourceLookahead;
            set => _sourceLookahead = value >= TimeSpan.Zero
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), "SourceLookahead can't be negative.");
        }

        private static int _readerBufferFrames = 8;

        /// <summary>
        /// How many frames each open reader decodes ahead of (behind, in
        /// reverse) the one being shown. Memory is roughly this many decoded
        /// frames per visible or upcoming media input.
        /// </summary>
        public static int ReaderBufferFrames
        {
            get => _readerBufferFrames;
            set => _readerBufferFrames = value >= 1
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), "ReaderBufferFrames must be at least 1.");
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
        /// frame (FfmpegArgs.BuildTransparentBlank) is built fresh on
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
        /// The per-filter mechanism (FfmpegArgs.ThreadPin and a `threads=1`
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