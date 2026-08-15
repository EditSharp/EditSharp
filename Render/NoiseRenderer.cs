using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using EditSharp;
using EditSharp.Components;
using EditSharp.Components.Clips;

namespace EditSharp.Render
{
    /// <summary>
    /// Renders a NoiseClip's whole `perlin` stream to a file, once, so the
    /// per-frame render can seek into it.
    ///
    /// This exists because `perlin` is a GENERATOR SOURCE with no seek: it
    /// produces frames strictly sequentially from frame 0, so asking it for
    /// output frame N (which the per-frame path did via
    /// trim=start_frame=N:end_frame=N+1) makes ffmpeg generate and throw away
    /// every frame before it. That is O(N) work to produce frame N, i.e.
    /// O(N^2) across a render — measured on a real render as a dead-linear
    /// +28ms per frame index at 1080p, which is almost exactly the cost of
    /// generating one 1080p perlin frame. Rendering the stream once here and
    /// seeking into it per frame is the same fix optimized media already
    /// applies to video sources, for the identical underlying problem.
    ///
    /// FFV1 in matroska for the same reasons OptimizedMediaBuilder uses it for
    /// video: lossless (noise is the input to everything downstream, so
    /// generation artefacts would compound), and intra-only so every frame is
    /// independently seekable with no GOP cost.
    /// </summary>
    internal static class NoiseRenderer
    {
        /// <summary>
        /// How many noise cells span the canvas at Detail = 1.
        ///
        /// The `perlin` source normalizes its coordinates by frame size —
        /// x = xscale * column / width — so xscale IS the number of noise cells
        /// across the frame, whatever the pixel resolution. That makes Detail
        /// resolution-independent for free: at Detail 1 a feature is 1/1000th of
        /// the canvas at 720p and at 4K alike, rather than 1/1000th at one and a
        /// solid wash at the other.
        ///
        /// Must stay in step with ClipContentBuilder's constant of the same
        /// name — the two paths have to produce identical noise for the same
        /// clip, or a timeline would look different depending on which one
        /// generated it.
        /// </summary>
        private const double DetailCellsPerCanvas = 1000.0;

        /// <summary>
        /// Noise cells traversed per second at SeetheRate = 1.
        ///
        /// `perlin` computes its time coordinate as tscale * seconds (pts times
        /// timebase), so this is framerate-independent the same way Detail is
        /// resolution-independent — the noise seethes at the same rate whether
        /// the timeline renders at 24 or 60fps.
        ///
        /// Same cross-path constraint as DetailCellsPerCanvas above.
        /// </summary>
        private const double SeetheCellsPerSecond = 10.0;

        /// <summary>
        /// The `perlin` source arguments for a clip at a given canvas size.
        /// Shared so every path that generates this clip's noise produces
        /// byte-identical output.
        /// </summary>
        public static string BuildPerlinSource(
            NoiseClip clip, int fps, int canvasWidth, int canvasHeight)
        {
            double xscale = Math.Max(clip.Detail, 0f) * DetailCellsPerCanvas;

            //cells are square in PIXELS only if the coordinate span is scaled by
            //the frame's aspect: x runs 0..xscale across the width and y runs
            //0..yscale across the height, so leaving them equal on a 16:9 canvas
            //would squash every blob vertically
            double yscale = xscale * canvasHeight / (double)canvasWidth;

            double tscale = Math.Max(clip.SeetheRate, 0f) * SeetheCellsPerSecond;

            //random_mode defaults to `random`, which generates its own seed and
            //ignores random_seed entirely — without selecting `seed` mode the
            //Seed property would be silently inert and every render would
            //differ. Cast to uint because the option is unsigned and Seed is a
            //signed int that a caller is free to set negative.
            uint seed = unchecked((uint)clip.Seed);

            return $"perlin=size={canvasWidth}x{canvasHeight}:rate={fps}:" +
                   $"random_mode=seed:random_seed={seed}:" +
                   $"xscale={GraphUtilities.Num(xscale)}:" +
                   $"yscale={GraphUtilities.Num(yscale)}:" +
                   $"tscale={GraphUtilities.Num(tscale)}";
        }

        /// <summary>
        /// Renders the clip's full duration and returns the temp file path.
        ///
        /// `perlin` has no duration option and generates indefinitely, so the
        /// length is taken with a trim — same as ClipContentBuilder does.
        /// </summary>
        public static async Task<string> RenderAsync(
            NoiseClip clip, int fps, int canvasWidth, int canvasHeight)
        {
            string outputPath = GraphUtilities.GetVideoTempFilePath($"noise_{Guid.NewGuid():N}.mkv");

            string filter =
                $"{BuildPerlinSource(clip, fps, canvasWidth, canvasHeight)}," +
                $"trim=duration={GraphUtilities.Num(clip.Duration.TotalSeconds)}," +
                $"setpts=PTS-STARTPTS,format={PixelFormats.Primary},settb=AVTB[out]";

            var args = new List<string>
            {
                "-y", "-v", "error",
                "-filter_complex", filter,
                "-map", "[out]",
                "-c:v", Constants.VideoCodecNames[VideoCodec.FFV1],
            };

            string? pixelFormat = VideoUtils.PixelFormatFor(VideoCodec.FFV1);
            if (pixelFormat != null)
            {
                args.Add("-pix_fmt");
                args.Add(pixelFormat);
            }

            //same small-cluster tuning optimized media uses, and for the same
            //reason — this file exists to be seeked into once per output frame,
            //so a seek must not have to decode forward from a distant cluster
            //boundary
            args.AddRange(VideoUtils.MuxerTuningArgsFor(VideoCodec.FFV1));

            args.Add("-an");
            args.Add(outputPath);

            var psi = new ProcessStartInfo
            {
                FileName = EditSharpConfig.FfmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"ffmpeg exited with code {process.ExitCode} rendering noise:\n{stderr}");

            return outputPath;
        }
    }
}
