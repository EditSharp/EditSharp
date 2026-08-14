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
