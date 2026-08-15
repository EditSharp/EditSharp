using EditSharp;
using System;
using System.Globalization;
using System.IO;

namespace EditSharp.Render
{
    /// <summary>
    /// Small shared helpers used across the assembly pipeline: number formatting
    /// for filter strings, the transparent-blank building block, and temp file
    /// paths.
    ///
    /// This file used to be much larger. The scale/rotation maths
    /// (ComputeScaledSize, ComputeRotatedSize, EvenAtLeast2), the whole family of
    /// supersampled rotation helpers, and NormalizeTimebase all existed to work
    /// around ffmpeg's `rotate` filter — its binary inside/outside edge test, and
    /// its habit of dragging colour toward the fill at partially transparent
    /// pixels. The timeline pipeline does every transform through a single
    /// `perspective` call instead, so none of that is reachable any more and has
    /// been removed rather than left to rot. ResolveSourceRangeAsync and
    /// ResolveOverlayWindow went with the Blueprint/Overlay model they served.
    /// </summary>
    internal static class GraphUtilities
    {
        public static string Sec(TimeSpan t) => Num(t.TotalSeconds);

        public static string Num(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

        public static double Clamp(double value, double min, double max) =>
            Math.Max(min, Math.Min(value, Math.Max(min, max)));

        /// <summary>
        /// A PER-FILTER thread pin, appended to an individual filter's options
        /// (e.g. <c>scale=1920:1080:flags=area:threads=1</c>) rather than
        /// applied to the whole graph.
        ///
        /// This is the surgical version of EditSharpConfig.FilterThreads, and
        /// exists because the global pin is a sledgehammer: it fixed the
        /// inter-thread seam artifact but cost roughly 3x per-frame render time
        /// on a real blueprint, because it also serialized every filter that was
        /// never implicated — most importantly the effect chain (gblur, blend),
        /// which is the expensive part of a frame with a drop shadow on it and
        /// which runs at FULL CANVAS on every frame (see ClipVideoChain's notes
        /// on PostTransform being deliberately out of the work-rect
        /// optimization's scope).
        ///
        /// `threads` is a GENERIC libavfilter option available on any filter
        /// supporting slice threading, not something each filter declares
        /// individually — verified directly against perspective, scale, overlay,
        /// gblur and blend, all of which accept it.
        ///
        /// WHICH filters carry this pin is a deliberate, and currently
        /// CONSERVATIVE, line: everything in the geometry/resampling path that
        /// was present in the confirmed minimal reproduction (a single
        /// GeneratorClip, no effects, Normal blend — which still showed all 15
        /// lines) is pinned, and nothing else is. That means every `scale`,
        /// `perspective` and `overlay` in ClipVideoChain and FrameFilterChain,
        /// while ClipEffects' own filters are left free to thread. If the lines
        /// ever return with EditSharpConfig.FilterThreads at 0, the culprit is a
        /// filter OUTSIDE this set and the honest move is to bisect rather than
        /// guess — set FilterThreads back to 1 to confirm the fix still holds,
        /// then widen this pin one filter at a time.
        /// </summary>
        public const string ThreadPin = "threads=1";

        /// <summary>
        /// The GLOBAL ffmpeg options pinning libavfilter's thread count, to be
        /// added to every invocation that runs a filter graph — see
        /// EditSharpConfig.FilterThreads for the full reasoning (short version:
        /// multi-threaded filter execution was leaving a zeroed-alpha seam at
        /// each inter-thread slice boundary, producing threads-1 black lines
        /// per frame).
        ///
        /// Returns EMPTY when EditSharpConfig.FilterThreads is 0, which is the
        /// "let ffmpeg use every core, and rely on the per-filter ThreadPin
        /// above for correctness instead" setting.
        ///
        /// BOTH options are emitted, and that is not redundancy: ffmpeg splits
        /// this across two separate knobs, and which one applies depends on how
        /// the graph was built. -filter_complex_threads governs a
        /// `-filter_complex` graph (FrameRenderer's per-frame render,
        /// NoiseRenderer, OptimizedMediaEffectsBaker) while -filter_threads
        /// governs a simple `-vf` pipeline (VideoUtils.ReencodeVideoAsync's
        /// scale). Setting only one leaves the other path running at ffmpeg's
        /// default of "one thread per logical core", so both are always sent
        /// rather than trying to predict per call site which is in play.
        ///
        /// These are GLOBAL options: they must appear before any -i, so callers
        /// add them at the head of the argument list alongside -y/-v, never
        /// after an input.
        /// </summary>
        public static string[] FilterThreadingArgs()
        {
            int configured = EditSharpConfig.FilterThreads;

            //0 means "unrestricted" — emit nothing and let ffmpeg pick, with
            //the per-filter ThreadPin carrying correctness on its own
            if (configured <= 0) return [];

            string threads = configured.ToString(CultureInfo.InvariantCulture);

            return ["-filter_threads", threads, "-filter_complex_threads", threads];
        }

        /// <summary>
        /// Builds a fully transparent RGBA frame of the given size and duration —
        /// the base every composite is drawn onto, and the backdrop a drop shadow
        /// is positioned against.
        ///
        /// The alpha is zeroed with colorchannelmixer rather than being built from
        /// a separate gray mask and alphamerge'd together. The mask approach looked
        /// safer but was quietly wrong: `color=black,format=gray` produces
        /// LIMITED-RANGE black, which is 16 rather than 0, so every "transparent"
        /// blank came out roughly 6% opaque. Invisible under a single overlay, but
        /// it tints the whole picture once channels are stacked on top of each
        /// other. Measured directly — the alphamerge form gives alpha=16, this
        /// gives 0.
        ///
        /// `color` is a real libavfilter source, usable inline inside
        /// filter_complex with no separate -i/-f lavfi input, which keeps the
        /// command line short on timelines with many of these.
        /// </summary>
        public static string BuildTransparentBlank(
            InputGraph graph, int width, int height, int fps, double duration, string labelPrefix)
        {
            string blankLabel = graph.NextLabel(labelPrefix);
            graph.FilterLines.Add(
                $"color=black:size={width}x{height}:rate={fps}:duration={Num(duration)}," +
                $"format={PixelFormats.Primary},colorchannelmixer=aa=0[{blankLabel}]");

            return blankLabel;
        }

        /// <summary>
        /// Paths for temp files, routed under EditSharpConfig.TempDirectory
        /// rather than Windows' %TEMP% — see that property's remarks for why:
        /// testing traced a native ffmpeg crash (0xc0000409, a stack buffer
        /// overrun) specifically to files living in %TEMP%, most likely a path-
        /// length issue in the "-/option" file-loading mechanism used for large
        /// filter graphs.
        ///
        /// The Image/Video subfolder is created lazily on first use rather than
        /// assumed to exist — EditSharp doesn't require the host app to set up
        /// any folder structure ahead of time. Directory.CreateDirectory is a
        /// no-op when the folder is already there, so this is cheap to call on
        /// every render.
        /// </summary>
        public static string GetImageTempFilePath(string fileName)
        {
            string dir = Path.Combine(EditSharpConfig.TempDirectory, "Image");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName);
        }

        public static string GetVideoTempFilePath(string fileName)
        {
            string dir = Path.Combine(EditSharpConfig.TempDirectory, "Video");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName);
        }
    }
}
