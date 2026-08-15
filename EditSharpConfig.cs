using System;

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

        private static int _filterThreads = 0;

        /// <summary>
        /// How many threads libavfilter may use to execute a filter graph
        /// (ffmpeg's -filter_threads / -filter_complex_threads).
        ///
        /// 0 (the default) means UNRESTRICTED — ffmpeg uses every logical core,
        /// and correctness is carried instead by the per-filter
        /// GraphUtilities.ThreadPin applied to the specific filters implicated
        /// below. 1 is the known-good sledgehammer: it fixes the artifact
        /// described here outright, at roughly 3x the per-frame render cost.
        /// Any other value pins the graph to exactly that many threads.
        ///
        /// THE ARTIFACT THIS EXISTS FOR:
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
        /// Setting this to 1 was confirmed to eliminate the lines. Worth
        /// knowing for anyone revisiting this: ffmpeg 8.0 (this project's
        /// target) ships a rewritten, multi-threaded swscale, so a `scale`
        /// that was effectively serial on the ffmpeg this pipeline was first
        /// written against is not serial any more.
        ///
        /// WHY THE DEFAULT IS 0 AND NOT 1: pinning the whole graph also
        /// serializes filters that were never implicated — above all the
        /// effect chain, which is where a full blueprint's per-frame time
        /// actually goes (a PostTransform drop shadow blurs at FULL CANVAS on
        /// every frame). Measured on a real blueprint, the global pin took
        /// per-frame time from 4-7s to over 15s. The per-filter ThreadPin
        /// keeps the fix while leaving that chain parallel.
        ///
        /// Note that filter-level parallelism is not the only lever, and
        /// arguably not the best one: this pipeline renders each frame in its
        /// own process, so parallelism is better spent at the PROCESS level
        /// (Blueprint.FrameRenderConcurrency, still defaulting to 1) than
        /// inside one frame's graph. Whole frames are perfectly independent,
        /// whereas filter slices have to stitch back together — which is the
        /// very thing that went wrong here.
        /// </summary>
        public static int FilterThreads
        {
            get => _filterThreads;
            set => _filterThreads = value >= 0
                ? value
                : throw new ArgumentOutOfRangeException(
                    nameof(value), "FilterThreads cannot be negative.");
        }
    }

    /// <summary>
    /// Minimal logging seam so EditSharp doesn't assume Console, a specific
    /// logging framework, or any other ambient logger exists in the host
    /// process. Adapt to Microsoft.Extensions.Logging, Serilog, etc. with a
    /// one-line wrapper — e.g. <c>msg => _logger.LogInformation(msg)</c>.
    /// </summary>
    public interface IEditSharpLogger
    {
        void Log(string message);

        void LogVerbose(string message);
    }

    internal sealed class NullEditSharpLogger : IEditSharpLogger
    {
        public static readonly NullEditSharpLogger Instance = new();
        private NullEditSharpLogger() { }
        public void Log(string message) { }
        public void LogVerbose(string message) { }
    }
}
