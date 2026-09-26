using System;
using EditSharp.Caching.Proxy;
using EditSharp.Video;

namespace EditSharp
{
    /// <summary>Process-wide settings. Set them once at startup; a change applies to everything running in the process from then on.</summary>
    public static class EditSharpConfig
    {
        private static string _tempDirectory = Path.Combine(AppContext.BaseDirectory, "Temp");

        /// <summary>Where EditSharp writes scratch files, such as re-encoded media.</summary>
        /// <remarks>Defaults to a Temp folder next to the application. Subfolders are created on first write, so the directory doesn't need to exist.</remarks>
        /// <exception cref="ArgumentNullException">Set to null.</exception>
        public static string TempDirectory
        {
            get => _tempDirectory;
            set => _tempDirectory = value ?? throw new ArgumentNullException(nameof(value));
        }

        private static string _proxyDirectory = Path.Combine(AppContext.BaseDirectory, "Proxy");

        /// <summary>Where proxies are stored, along with the media hash index.</summary>
        /// <remarks>EditSharp never deletes it, so pointing it somewhere durable lets projects reuse proxies across runs.</remarks>
        /// <exception cref="ArgumentNullException">Set to null.</exception>
        public static string ProxyDirectory
        {
            get => _proxyDirectory;
            set => _proxyDirectory = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>The format <see cref="ProxyCache.BuildAsync"/> builds when none is named.</summary>
        /// <remarks>The .esrp formats read without starting ffmpeg and allow true random access; DNxHR and ProRes are for use in other software and read through ffmpeg.</remarks>
        public static ProxyFormat ProxyFormat { get; set; } = ProxyFormat.EsrpDelta7;

        private static int _proxyMaxDimension = 1280;

        /// <summary>The longest side a proxy is built at, in pixels; smaller sources aren't scaled up.</summary>
        /// <remarks>Proxies keep the source's frame rate, so one proxy serves scrubbing and playback.</remarks>
        /// <exception cref="ArgumentOutOfRangeException">Set to zero or less.</exception>
        public static int ProxyMaxDimension
        {
            get => _proxyMaxDimension;
            set => _proxyMaxDimension = value > 0
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), "ProxyMaxDimension must be positive.");
        }

        /// <summary>How .esrp proxies compress each frame. Applies to proxies built after the change.</summary>
        public static EsrpCompressionScheme EsrpCompressionScheme { get; set; } = EsrpCompressionScheme.Zstd;

        private static int _esrpCompressionLevel = 9;

        /// <summary>The Zstd level for .esrp proxy frames, 1 to 22.</summary>
        /// <remarks>Higher is smaller and slower to build: on 1280x720 frames, 9 builds about 18 times faster than 19 for about 10% more disk. Reading speed is the same at any level.</remarks>
        /// <exception cref="ArgumentOutOfRangeException">Set outside 1 to 22.</exception>
        public static int EsrpCompressionLevel
        {
            get => _esrpCompressionLevel;
            set => _esrpCompressionLevel = value is >= 1 and <= 22
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), "EsrpCompressionLevel must be between 1 and 22.");
        }

        private static int _maxConcurrentProxyBuilds = 1;

        /// <summary>How many proxies may build at once; further builds wait in order.</summary>
        /// <remarks>Read when a queued build starts, so a change applies to builds that haven't started yet.</remarks>
        /// <exception cref="ArgumentOutOfRangeException">Set below 1.</exception>
        public static int MaxConcurrentProxyBuilds
        {
            get => _maxConcurrentProxyBuilds;
            set => _maxConcurrentProxyBuilds = value >= 1
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), "MaxConcurrentProxyBuilds must be at least 1.");
        }

        private static Time _sourceRetryInterval = Time.FromSeconds(2);

        /// <summary>How long a preview waits before retrying a source that was offline or failed to decode.</summary>
        /// <remarks>A drive may be reconnected or a file re-exported in the meantime. Exports never retry.</remarks>
        /// <exception cref="ArgumentOutOfRangeException">Set to zero or less.</exception>
        public static Time SourceRetryInterval
        {
            get => _sourceRetryInterval;
            set => _sourceRetryInterval = value > Time.Zero
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), "SourceRetryInterval must be positive.");
        }

        private static Time _sourceLookahead = Time.FromSeconds(2);

        /// <summary>How far ahead of the playhead (behind it, in reverse) sources are prepared and their readers opened.</summary>
        /// <remarks>A clip that arrives on screen then already has frames waiting.</remarks>
        /// <exception cref="ArgumentOutOfRangeException">Set below zero.</exception>
        public static Time SourceLookahead
        {
            get => _sourceLookahead;
            set => _sourceLookahead = value >= Time.Zero
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), "SourceLookahead can't be negative.");
        }

        private static int _audioBlockFrames = 512;

        /// <summary>How many audio frames the engine processes per tick, 32 to 16384.</summary>
        /// <remarks>Parameter changes take effect within one block; 512 frames is about 10.7 ms at 48 kHz. Read when a session starts.</remarks>
        /// <exception cref="ArgumentOutOfRangeException">Set outside 32 to 16384.</exception>
        public static int AudioBlockFrames
        {
            get => _audioBlockFrames;
            set => _audioBlockFrames = value is >= 32 and <= 16384
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), "AudioBlockFrames must be between 32 and 16384.");
        }

        private static Time _audioLatency = Time.FromMilliseconds(100);

        /// <summary>How far ahead of the speakers playback renders audio, which is also how long an edit takes to be heard.</summary>
        /// <remarks>Read when a session starts.</remarks>
        /// <exception cref="ArgumentOutOfRangeException">Set to zero or less.</exception>
        public static Time AudioLatency
        {
            get => _audioLatency;
            set => _audioLatency = value > Time.Zero
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), "AudioLatency must be positive.");
        }

        private static int _readerBufferFrames = 8;

        /// <summary>How many frames each open reader decodes ahead of the one shown (behind it, in reverse).</summary>
        /// <remarks>Memory use is about this many decoded frames per visible or upcoming source.</remarks>
        /// <exception cref="ArgumentOutOfRangeException">Set below 1.</exception>
        public static int ReaderBufferFrames
        {
            get => _readerBufferFrames;
            set => _readerBufferFrames = value >= 1
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), "ReaderBufferFrames must be at least 1.");
        }

        /// <summary>Where EditSharp reports progress and diagnostics. Logs nothing until set.</summary>
        public static IEditSharpLogger Logger { get; set; } = NullEditSharpLogger.Instance;

        /// <summary>The ffmpeg executable: a name found on PATH, or a full path.</summary>
        public static string FfmpegPath { get; set; } = "ffmpeg";

        /// <summary>The ffprobe executable: a name found on PATH, or a full path.</summary>
        public static string FfprobePath { get; set; } = "ffprobe";

        private static int _filterThreads = 1;

        /// <summary>How many threads ffmpeg's filters may use (its -filter_threads and -filter_complex_threads).</summary>
        /// <remarks>Defaults to 1 because ffmpeg splitting a frame across threads left a one-pixel row of zero alpha at every seam between thread slices, which showed as thin horizontal lines. Raise it only while checking output for those lines.</remarks>
        /// <exception cref="ArgumentOutOfRangeException">Set below 1.</exception>
        public static int FilterThreads
        {
            get => _filterThreads;
            set => _filterThreads = value >= 1
                ? value
                : throw new ArgumentOutOfRangeException(
                    nameof(value), "FilterThreads must be at least 1.");
        }

        /// <summary>Which swscale backends ffmpeg 9 and later may use for scaling and pixel format conversion (its -sws_backends).</summary>
        /// <remarks>Defaults to "x86", which opts into ffmpeg's SIMD backend; ffmpeg's own default only uses the plain C one. ffmpeg marks x86 unstable, so if output shows seams, banding or wrong alpha, set this to "stable".</remarks>
        public static string SwsBackends { get; set; } = "x86";
    }

    /// <summary>Where EditSharp sends log messages. Wrap your own logging framework in it.</summary>
    /// <remarks>For Microsoft.Extensions.Logging, for example, <see cref="Log"/> can call <c>_logger.LogInformation(message)</c>.</remarks>
    public interface IEditSharpLogger
    {
        /// <summary>Information a user cares about, such as a render finishing.</summary>
        /// <param name="message">The message.</param>
        void Log(string message);

        /// <summary>Step-by-step detail for debugging, such as each rendered frame.</summary>
        /// <param name="message">The message.</param>
        void LogVerbose(string message);

        /// <summary>A problem that was worked around, such as falling back from the GPU.</summary>
        /// <param name="message">The message.</param>
        void LogWarning(string message);

        /// <summary>A failure that stopped an operation, such as ffmpeg crashing mid-render.</summary>
        /// <param name="message">The message.</param>
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
