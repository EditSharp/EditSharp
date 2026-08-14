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
                $"format={PixelFormats.Rgba},colorchannelmixer=aa=0[{blankLabel}]");

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
