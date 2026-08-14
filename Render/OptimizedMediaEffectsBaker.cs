using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using EditSharp;
using EditSharp.Components;
using EditSharp.Components.Effects;

namespace EditSharp.Render
{
    /// <summary>
    /// Bakes a clip's EffectStage.PreTransform effects into its optimized
    /// media, once, at build time — see OptimizedMediaBuilder for why (every
    /// per-frame process was otherwise redoing identical PreTransform work on
    /// every frame the clip is visible on).
    ///
    /// This deliberately does NOT live on VideoUtils. VideoUtils is meant to
    /// stay a small, generic, public building block — re-encode, mux, resize,
    /// the kind of thing a consumer might reach for directly and that has
    /// nothing to do with the timeline pipeline's own internals. Baking a
    /// specific clip's specific Effect list against a bake-time
    /// ClipChainContext is neither generic nor something a consumer calls
    /// directly; it's this one pipeline stage's own implementation detail, so
    /// it gets its own internal-only file instead of growing VideoUtils'
    /// public surface into something pipeline-specific.
    ///
    /// Only EffectStage.PreTransform effects are valid input here: a
    /// PostTransform effect (DropShadowEffect by default) depends on the
    /// clip's time-varying position within the CANVAS, which doesn't exist
    /// yet at this stage — see ClipVideoChain's PreTransform/PostTransform
    /// split for the full reasoning. OptimizedMediaBuilder is what filters
    /// the effect list down to PreTransform-only before calling here; this
    /// class doesn't re-check that itself.
    /// </summary>
    internal static class OptimizedMediaEffectsBaker
    {
        /// <summary>
        /// Re-encodes source to codec with an optional resize (scaleTo, same
        /// contract as VideoUtils.ReencodeVideoAsync's) and preTransformEffects
        /// applied once over the whole clip.
        ///
        /// canvasWidth/canvasHeight/fps normalize effect parameters (blur
        /// radius, mask size) the same way the per-frame path does — a blur
        /// baked here must come out the same size it would have if applied
        /// per-frame, so these need to be the REAL render canvas, not the
        /// output buffer's own (possibly smaller, post-scale) size.
        ///
        /// tempFiles should be the render's own bag (the one FrameRenderer
        /// sweeps at the end) — baking can create a new mask file (see
        /// ClipEffects.MaskCache) that isn't part of anything this method
        /// returns, so it needs its own path into cleanup.
        /// </summary>
        public static async Task<string> BakeAsync(
            Source source, VideoCodec codec,
            (int Width, int Height)? scaleTo,
            List<Effect> preTransformEffects,
            int canvasWidth, int canvasHeight, int fps,
            ConcurrentBag<string> tempFiles)
        {
            if (source.Type != SourceType.Video)
                throw new ArgumentException($"'{source.Path}' is not a Video source.", nameof(source));

            if (!File.Exists(source.Path))
                throw new FileNotFoundException($"Input not found: {source.Path}", source.Path);

            if (!Constants.VideoCodecNames.TryGetValue(codec, out string? encoderName))
                throw new NotSupportedException($"BakeAsync has no encoder mapping for {codec}.");

            EditSharpConfig.Logger.LogVerbose(
                $"OptimizedMediaEffectsBaker starting for '{source.Path}' " +
                $"({preTransformEffects.Count} effect(s))...");
            var stageSw = Stopwatch.StartNew();

            string extension = VideoUtils.ContainerExtensionFor(codec);
            string outputPath = GraphUtilities.GetVideoTempFilePath($"reencode_{Guid.NewGuid():N}.{extension}");

            //effects need a real filter graph (ClipEffects.ApplyStage builds
            //its chains against an InputGraph, the same machinery the
            //per-frame path uses), so the input goes through InputGraph
            //rather than a bare -ss/-i pair
            var graph = new InputGraph();
            int index = graph.AddInput(source.Path, true, VideoUtils.TrimArgsFor(source));

            string current = $"{index}:v";
            (int Width, int Height)? bufferSize = scaleTo;

            if (scaleTo is { } size)
            {
                //scaled at whatever pixel format the decoder handed back —
                //deliberately BEFORE the format conversion below, not after.
                //Converting to PixelFormats.Primary first and scaling second
                //would pay that conversion's cost (no accelerated swscale
                //path exists between some codecs' native decode format and
                //Primary, confirmed by direct benchmark) at the full native
                //resolution; scaling first means that unavoidable conversion
                //runs on the now-smaller buffer instead
                string scaled = graph.NextLabel("optscale");
                graph.FilterLines.Add(
                    $"[{current}]scale={size.Width}:{size.Height},setsar=1[{scaled}]");
                current = scaled;
            }
            else
            {
                //no scale requested — probe the native size so the bake
                //context's own buffer dimensions (used to normalize effect
                //parameters) are accurate rather than 0x0
                MediaInfo native = await MediaProbe.ProbeAsync(source.Path);
                bufferSize = (native.Width, native.Height);
            }

            string formatted = graph.NextLabel("optbase");
            graph.FilterLines.Add($"[{current}]format={PixelFormats.Primary},settb=AVTB[{formatted}]");
            current = formatted;

            //the bake context's "frame" IS the clip's own buffer, full stop —
            //no canvas padding, since there is no perspective warp at this
            //stage needing edge room to clamp into (that only happens
            //per-frame, after this optimized media already exists).
            //CanvasWidth/Height stay the REAL render canvas so normalized
            //parameters (blur radius, mask radius) come out the same size
            //they would per-frame; RepeatStaticInputs=true is what makes
            //ApplyRoundedCorners loop its mask input across every frame of
            //this continuous re-encode instead of reading it once
            var placement = new TransformExpressions.ContentPlacement(
                bufferSize!.Value.Width, bufferSize.Value.Height, 0, 0);
            var workRect = new TransformExpressions.WorkRect(
                0, 0, bufferSize.Value.Width, bufferSize.Value.Height);

            var context = new ClipChainContext(
                canvasWidth, canvasHeight, workRect, fps,
                source.Duration?.TotalSeconds ?? 999999,
                placement, graph, tempFiles, repeatStaticInputs: true);

            string mapLabel = ClipEffects.ApplyStage(
                preTransformEffects, EffectStage.PreTransform, current, context);

            long graphBuildMs = stageSw.ElapsedMilliseconds;

            var args = new List<string> { "-y", "-v", "error" };

            foreach (var input in graph.Inputs)
            {
                if (input.ExtraArgs != null) args.AddRange(input.ExtraArgs);
                args.Add("-i");
                args.Add(input.Path);
            }

            args.Add("-filter_complex");
            args.Add(string.Join(";", graph.FilterLines));
            args.Add("-map");
            args.Add($"[{mapLabel}]");

            args.Add("-c:v");
            args.Add(encoderName);

            string? pixelFormat = VideoUtils.PixelFormatFor(codec);
            if (pixelFormat != null)
            {
                args.Add("-pix_fmt");
                args.Add(pixelFormat);
            }

            //see VideoUtils.MuxerTuningArgsFor — forces small matroska
            //clusters so a later per-frame -ss seek into this file doesn't
            //pay a forward-decode cost that grows with how far into the
            //clip the target is
            args.AddRange(VideoUtils.MuxerTuningArgsFor(codec));

            //video only — matches VideoUtils.ReencodeVideoAsync; the whole
            //timeline's audio is still mixed separately, once, in AudioMixer
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

            var spawnSw = Stopwatch.StartNew();
            process.Start();
            long spawnMs = spawnSw.ElapsedMilliseconds;

            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            var runSw = Stopwatch.StartNew();
            await process.WaitForExitAsync();
            long runMs = runSw.ElapsedMilliseconds;

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"ffmpeg exited with code {process.ExitCode}:\n{stderr}");

            EditSharpConfig.Logger.LogVerbose(
                $"OptimizedMediaEffectsBaker for '{source.Path}' done: graph build {graphBuildMs}ms, " +
                $"spawn {spawnMs}ms, run {runMs}ms ({graph.Inputs.Count} input(s), " +
                $"{graph.FilterLines.Count} filter line(s)) -> {outputPath}");

            return outputPath;
        }
    }
}
