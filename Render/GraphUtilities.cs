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
        /// The GLOBAL ffmpeg options pinning libavfilter's thread count, to be
        /// added to every invocation that runs a filter graph.
        ///
        /// THIS EXISTS AS A CORRECTNESS FIX — see EditSharpConfig.FilterThreads
        /// for the full history. Short version: multi-threaded filter execution
        /// was leaving a zeroed-alpha seam at each inter-thread slice boundary,
        /// producing threads-1 evenly-spaced black lines confined to one half
        /// of the frame. Confirmed on a 16-core machine as exactly 15 lines
        /// (16 threads, 15 interior slice boundaries) starting exactly at
        /// canvas-width/2.
        ///
        /// A per-filter version of this (a `threads=1` pin on individual
        /// filters, leaving the rest of the graph free to thread) was tried and
        /// reverted. It looked justified by an early benchmark showing the
        /// global pin costing roughly 3x per-frame time on a real blueprint,
        /// but that comparison turned out to be invalid — it was run with
        /// Blueprint.FrameRenderConcurrency at 1 for the baseline and 4 for the
        /// pinned case. Once both sides were measured at the same concurrency,
        /// the global pin's actual cost was negligible, on this blueprint and
        /// others. The per-filter version was strictly more code for no real
        /// benefit it was purchased under a false premise, so it was removed
        /// rather than kept "just in case" — see EditSharp-Handoff.md for
        /// anyone who finds a stray `ThreadPin` reference in history and
        /// wonders where it went.
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
        ///
        /// ALSO carries -sws_backends (EditSharpConfig.SwsBackends), which is
        /// a different knob answering a different question — see that
        /// property's remarks for the full reasoning. Short version:
        /// -filter_threads/-filter_complex_threads control whether libavfilter
        /// SLICES a frame across multiple threads (the thing that caused the
        /// seam bug above); -sws_backends controls which SIMD kernel swscale
        /// uses to do the per-pixel math WITHIN whichever thread(s) end up
        /// running. Pinning thread count to 1 doesn't touch which backend that
        /// one thread uses, so the two settings are orthogonal and both belong
        /// in this same "global args, must precede any -i" bucket rather than
        /// being split into a second method callers would have to remember to
        /// also add.
        /// </summary>
        public static string[] FilterThreadingArgs()
        {
            string threads = Math.Max(1, EditSharpConfig.FilterThreads)
                .ToString(CultureInfo.InvariantCulture);

            return
            [
                "-filter_threads", threads,
                "-filter_complex_threads", threads,
                "-sws_backends", EditSharpConfig.SwsBackends,
            ];
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
