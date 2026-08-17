using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EditSharp;
using EditSharp.Components;
using EditSharp.Components.Clips;
using EditSharp.Components.Transitions;

namespace EditSharp.Render
{
    /// <summary>
    /// Entry point for the render strategy — item 13's full rewrite of the
    /// render loop, wiring items 3-12's Skia compositor into an actual
    /// end-to-end render for the first time.
    ///
    /// Shape of a render, POST-rewrite:
    ///   1. Probe every video/image source's native size (MediaProbe) and
    ///      rasterize every TextClip's PNG (TextRasterizer) — everything
    ///      about a clip that's constant across its whole life, done once
    ///      up front exactly as before. NO OptimizedMediaBuilder step
    ///      anymore — that pre-render pass existed to give independent
    ///      per-frame ffmpeg processes fast random-access seeks (items 10,
    ///      11), and there is no such process left to serve.
    ///   2. Render every output frame SEQUENTIALLY (no concurrency gate —
    ///      see below) directly against an in-process SKCanvas
    ///      (SkFrameCompositor), appending each frame's raw RGBA8888 bytes
    ///      to a single growing lossless accumulator file. A video clip's
    ///      SkSourceDecoder is opened on its first visible frame and
    ///      disposed once its visible window ends (SkClipContentSource).
    ///   3. Mux that accumulated video against the timeline's audio
    ///      (FinalizeOutputAsync) and encode to Blueprint's chosen codec —
    ///      the one ffmpeg subprocess step left on the video side, and the
    ///      only lossy step in the whole pipeline.
    ///
    /// WHY FULLY SEQUENTIAL, NOT JUST "CONCURRENCY DEFAULTS TO 1": item 11
    /// already decided FrameRenderConcurrency (parallel OUTPUT frames) is
    /// incompatible with a single ordered pipe per video source and
    /// dropped the Blueprint property, but left FrameRenderer's own
    /// semaphore/task-array machinery in place, gated at 1. That machinery
    /// has no remaining purpose now that RenderFrameAsync's replacement
    /// (SkFrameCompositor.RenderFrame) is synchronous, in-process Skia
    /// work rather than an awaited ffmpeg subprocess — there's nothing left
    /// to overlap. Removed outright here, not just left gated at 1.
    /// </summary>
    public static class Renderer
    {
        public static async Task RenderAsync(Blueprint blueprint)
        {
            Validate(blueprint);

            var tempFiles = new ConcurrentBag<string>();
            var sw = Stopwatch.StartNew();

            EditSharpConfig.Logger.Log("Starting render.");

            try
            {
                await RenderCoreAsync(blueprint, tempFiles);
                EditSharpConfig.Logger.Log($"Render complete in {sw.Elapsed}.");
            }
            finally
            {
                foreach (string path in tempFiles)
                {
                    try { File.Delete(path); } catch { /* best-effort cleanup */ }
                }
            }
        }

        private static async Task RenderCoreAsync(Blueprint blueprint, ConcurrentBag<string> tempFiles)
        {
            Timeline timeline = blueprint.Timeline;
            int width = blueprint.Resolution.Item1;
            int height = blueprint.Resolution.Item2;
            int fps = blueprint.Framerate;

            //ConcurrentDictionary rather than plain Dictionary: PrepareContentAsync
            //below runs one task per clip needing probing/rasterizing, all
            //writing into these same two dictionaries concurrently. The old
            //PrepareStaticContentAsync had this exact same shape and used a
            //plain Dictionary, which is not safe for concurrent writes even to
            //distinct keys (internal resize can race) — fixed here in passing,
            //not a behaviour change worth its own checklist item.
            var nativeSizes = new ConcurrentDictionary<Clip, (int, int)>();
            var staticImagePaths = new ConcurrentDictionary<Clip, string>();
            var decodePlans = new ConcurrentDictionary<Clip, DecodeHwAccelPlan>();

            var prepSw = Stopwatch.StartNew();
            await PrepareContentAsync(
                timeline, width, height, blueprint.HardwareAccelerator,
                nativeSizes, staticImagePaths, decodePlans, tempFiles);
            EditSharpConfig.Logger.LogVerbose($"Content prepared in {prepSw.ElapsedMilliseconds}ms.");

            //when a video clip's decoder can be torn down — computed once,
            //up front, from each clip's own known End time. Same role as the
            //old OptimizedMediaBuilder deletion schedule, keyed on Clip
            //identity instead of a media file path since there's no file to
            //delete anymore, only a subprocess to kill
            Dictionary<int, List<Clip>> decoderReleaseSchedule =
                BuildDecoderReleaseSchedule(timeline, fps);

            int totalFrames = Math.Max(1, (int)Math.Ceiling(timeline.Duration.TotalSeconds * fps));

            string accumulatorPath = GraphUtilities.GetVideoTempFilePath($"frames_{Guid.NewGuid():N}.raw");
            tempFiles.Add(accumulatorPath);

            EditSharpConfig.Logger.Log(
                $"Rendering {totalFrames} frame(s) at {width}x{height}@{fps}fps " +
                "(sequential, in-process Skia compositor).");

            using var contentSource = new SkClipContentSource(
                fps, nativeSizes, staticImagePaths, decodePlans);

            // One GRContext (or null -> software raster) for the whole render
            // session, and one surface pool sitting on top of it — both live
            // exactly as long as the frame loop below, since nothing about
            // either is safe to share across separate renders (a GRContext
            // wraps a real GPU device/command-queue handle; the pool's
            // contents are only valid while that context is). Seeded with one
            // canvas-sized surface per channel — see SkSurfacePool's own
            // remarks for why that count, specifically.
            using GpuContext gpuContext = GpuContext.Create(blueprint.HardwareAccelerator);
            using var surfacePool = new SkSurfacePool(
                gpuContext.GRContext, width, height, timeline.Channels.Count);

            using (var accumulator = new FileStream(
                accumulatorPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 1 << 20))
            {
                await RenderAllFramesAsync(
                    timeline, fps, width, height, nativeSizes, contentSource,
                    decoderReleaseSchedule, totalFrames, accumulator, surfacePool);
            }

            EditSharpConfig.Logger.Log("Finalizing output (mux + encode)...");
            var finalizeSw = Stopwatch.StartNew();
            await FinalizeOutputAsync(accumulatorPath, width, height, fps, blueprint, tempFiles);
            EditSharpConfig.Logger.Log($"Finalize complete in {finalizeSw.ElapsedMilliseconds}ms.");
        }

        /// <summary>
        /// Renders every output frame, strictly in order, writing each to
        /// the accumulator as it's produced. No concurrency gate, no task
        /// array — see the class remarks for why that machinery had nothing
        /// left to overlap once RenderFrameAsync's ffmpeg subprocess was
        /// replaced with synchronous in-process Skia work. Strict order is
        /// still required for two reasons, same as before: the accumulator
        /// is a headerless raw stream with no per-frame timestamps, and
        /// every active SkSourceDecoder must see its frames requested in
        /// increasing order (see SkSourceDecoder's own "sequentially
        /// forward" contract).
        /// </summary>
        private static async Task RenderAllFramesAsync(
            Timeline timeline, int fps, int width, int height,
            ConcurrentDictionary<Clip, (int, int)> nativeSizes,
            SkClipContentSource contentSource,
            Dictionary<int, List<Clip>> decoderReleaseSchedule,
            int totalFrames, Stream accumulator, SkSurfacePool surfacePool)
        {
            var sw = Stopwatch.StartNew();

            long previousElapsedMs = 0;

            for (int frameIndex = 0; frameIndex < totalFrames; frameIndex++)
            {
                FrameState state = FrameStateResolver.Resolve(timeline, frameIndex, fps, nativeSizes);

                (byte[] buffer, int length) = SkFrameCompositor.RenderFrame(
                    state, contentSource, width, height, fps, surfacePool);

                try
                {
                    await accumulator.WriteAsync(buffer.AsMemory(0, length));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                if (decoderReleaseSchedule.TryGetValue(frameIndex, out List<Clip>? finished))
                {
                    foreach (Clip clip in finished) contentSource.ReleaseDecoder(clip);
                }

                long currentElapsedMs = sw.ElapsedMilliseconds;
                long frameDeltaMs = currentElapsedMs - previousElapsedMs;
                previousElapsedMs = currentElapsedMs;

                //Both numbers together, not just cumulative — cumulative alone
                //means reading per-frame cost requires subtracting consecutive
                //log lines by hand, which every real profiling pass in this
                //project so far has had to do manually. Matches the shape the
                //old ffmpeg-based renderer's own progress output already had.
                EditSharpConfig.Logger.LogVerbose(
                    $"Rendered frame {frameIndex + 1}/{totalFrames} " +
                    $"({frameDeltaMs}ms this frame, {currentElapsedMs}ms elapsed).");
            }
        }

        /// <summary>
        /// Everything about a clip that's constant across its whole life:
        /// a video/image SourceClip's native pixel size (MediaProbe — the
        /// SAME probe call now covers both, where the old code split video
        /// sizing into OptimizedMediaBuilder's own probe and image sizing
        /// into this method), and a TextClip's rasterized PNG (built
        /// exactly ONCE — Content never changes mid-clip, so re-rasterizing
        /// identical text on every frame it's visible would be pure waste).
        ///
        /// GeneratorClip/NoiseClip need no entry at all — SkFrameCompositor
        /// .DrawClip sizes them to the canvas directly (see FrameClip's own
        /// remarks on why NativeWidth/Height == 0 is the correct signal for
        /// those two, not a missing-data bug).
        /// </summary>
        private static Task PrepareContentAsync(
            Timeline timeline, int canvasWidth, int canvasHeight, HardwareAccelerator hwAccel,
            ConcurrentDictionary<Clip, (int, int)> nativeSizes,
            ConcurrentDictionary<Clip, string> staticImagePaths,
            ConcurrentDictionary<Clip, DecodeHwAccelPlan> decodePlans,
            ConcurrentBag<string> tempFiles)
        {
            var tasks = new List<Task>();

            foreach (Channel channel in timeline.Channels)
            {
                foreach (Clip clip in channel.Clips.Values)
                {
                    switch (clip)
                    {
                        case SourceClip { Source.Type: SourceType.Video } video:
                            tasks.Add(ProbeVideoAsync(clip, video, hwAccel, nativeSizes, decodePlans));
                            break;

                        case SourceClip { Source.Type: SourceType.Image } image:
                            tasks.Add(PrepareImageAsync(clip, image, nativeSizes, staticImagePaths));
                            break;

                        case TextClip text:
                            PrepareText(clip, text, canvasWidth, canvasHeight,
                                nativeSizes, staticImagePaths, tempFiles);
                            break;
                    }
                }
            }

            return Task.WhenAll(tasks);
        }

        private static async Task ProbeVideoAsync(
            Clip clip, SourceClip video, HardwareAccelerator hwAccel,
            ConcurrentDictionary<Clip, (int, int)> nativeSizes,
            ConcurrentDictionary<Clip, DecodeHwAccelPlan> decodePlans)
        {
            (int width, int height) = await MediaProbe.GetDimensionsAsync(video.Source.Path);
            nativeSizes[clip] = (width, height);

            // Resolved once, up front, alongside the size probe this was
            // already paying an async round-trip for — not re-resolved per
            // frame or per decoder open (see SkSourceDecoder.Start's own
            // remarks on why the result is just passed straight through).
            decodePlans[clip] = await FfmpegRunner.GetDecodePlanAsync(video.Source.Path, hwAccel);
        }

        private static async Task PrepareImageAsync(
            Clip clip, SourceClip imageClip,
            ConcurrentDictionary<Clip, (int, int)> nativeSizes,
            ConcurrentDictionary<Clip, string> staticImagePaths)
        {
            (int width, int height) = await MediaProbe.GetDimensionsAsync(imageClip.Source.Path);
            nativeSizes[clip] = (width, height);

            //no re-encode/copy needed anymore — SkClipContentSource decodes
            //this path directly with SkiaSharp, so the original file itself
            //is the "static image", not a temp copy of it
            staticImagePaths[clip] = imageClip.Source.Path;
        }

        private static void PrepareText(
            Clip clip, TextClip text, int canvasWidth, int canvasHeight,
            ConcurrentDictionary<Clip, (int, int)> nativeSizes,
            ConcurrentDictionary<Clip, string> staticImagePaths,
            ConcurrentBag<string> tempFiles)
        {
            string path = TextRasterizer.Rasterize(
                text, canvasWidth, canvasHeight, out int width, out int height);

            tempFiles.Add(path);
            nativeSizes[clip] = (width, height);
            staticImagePaths[clip] = path;
        }

        /// <summary>
        /// The frame index at which each video clip's decoder can be torn
        /// down — the LAST frame that clip is visible on, computed once
        /// from Clip.End rather than discovered incrementally. A clip's
        /// decoder is opened lazily on its first GetContent call
        /// (SkClipContentSource) and released here on its last.
        /// </summary>
        private static Dictionary<int, List<Clip>> BuildDecoderReleaseSchedule(Timeline timeline, int fps)
        {
            var schedule = new Dictionary<int, List<Clip>>();

            foreach (Channel channel in timeline.Channels)
            {
                foreach (Clip clip in channel.Clips.Values)
                {
                    if (clip is not SourceClip { Source.Type: SourceType.Video }) continue;

                    int lastVisibleFrame = Math.Max(0, (int)Math.Ceiling(clip.End.TotalSeconds * fps) - 1);

                    if (!schedule.TryGetValue(lastVisibleFrame, out List<Clip>? list))
                        schedule[lastVisibleFrame] = list = [];

                    list.Add(clip);
                }
            }

            return schedule;
        }

        /// <summary>
        /// Muxes the accumulated lossless video against the timeline's audio
        /// and encodes to Blueprint's chosen codec — the one lossy step in
        /// the whole pipeline, and only when the codec itself is lossy.
        ///
        /// Audio is built the same way it always has: a fresh InputGraph,
        /// ClipContentBuilder per clip, AudioMixer.Compose — none of that
        /// changed by this migration at all. The only thing item 13 touches
        /// here is the VIDEO side's rawvideo input args: pix_fmt/byte-size
        /// now describe SkOutputFormat's rgba8888 accumulator, not
        /// PixelFormats.Primary's gbrap16le.
        /// </summary>
        private static async Task FinalizeOutputAsync(
            string accumulatorPath, int width, int height, int fps,
            Blueprint blueprint, ConcurrentBag<string> tempFiles)
        {
            var audioGraph = new InputGraph();
            var contents = new Dictionary<Clip, ClipContent>();

            //The accumulator occupies -i index 0 below, so it has to occupy
            //index 0 in THIS graph too before any clip is built — see the
            //original comment this is carried over from for the full
            //reasoning (InputGraph hands out indices in call order and
            //ClipContentBuilder bakes them straight into filter labels).
            _ = audioGraph.AddInput(accumulatorPath, verifyExists: false);

            foreach (Channel channel in blueprint.Timeline.Channels)
            {
                foreach (Clip clip in channel.Clips.Values)
                {
                    if (contents.ContainsKey(clip)) continue;

                    contents[clip] = await ClipContentBuilder.BuildAsync(
                        clip, audioGraph, width, height, fps, tempFiles, audioOnly: true);
                }
            }

            string audioLabel = AudioMixer.Compose(blueprint.Timeline, contents, audioGraph);

            bool isGif = blueprint.VideoCodec == VideoCodec.GIF;
            (string videoEncoderName, List<string> videoQualityArgs) =
                await FfmpegRunner.GetVideoEncoderSettingsAsync(
                    blueprint.VideoCodec, blueprint.HardwareAccelerator);

            string filterComplex = string.Join(";", audioGraph.FilterLines);
            string scriptPath = GraphUtilities.GetVideoTempFilePath($"audiofilter_{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(scriptPath, filterComplex);

            var args = new List<string> { "-y", "-v", "error" };
            args.AddRange(GraphUtilities.FilterThreadingArgs());

            //input 0: the accumulated frames. Headerless raw data at
            //SkOutputFormat's rgba8888, straight alpha, packed with no row
            //padding — exactly what SkFrameCompositor.ReadRgba8888 wrote
            args.AddRange(new[]
            {
                "-f", "rawvideo",
                "-pix_fmt", SkOutputFormat.FfmpegPixelFormat,
                "-s", $"{width}x{height}",
                "-r", fps.ToString(CultureInfo.InvariantCulture),
                "-i", accumulatorPath,
            });

            foreach (var input in audioGraph.Inputs.Skip(1))
            {
                if (input.ExtraArgs != null) args.AddRange(input.ExtraArgs);
                args.Add("-i");
                args.Add(input.Path);
            }

            args.Add("-/filter_complex");
            args.Add(scriptPath);

            args.Add("-map");
            args.Add("0:v");

            if (!isGif)
            {
                args.Add("-map");
                args.Add($"[{audioLabel}]");
            }

            if (isGif)
            {
                args.Add("-c:v");
                args.Add("gif");
            }
            else
            {
                args.Add("-c:v");
                args.Add(videoEncoderName);
                args.AddRange(videoQualityArgs);

                string audioCodecName = Constants.AudioCodecNames[blueprint.AudioCodec];
                args.Add("-c:a");
                args.Add(audioCodecName);
                args.Add("-b:a");
                args.Add("192k");
                args.Add("-shortest");
                args.Add("-pix_fmt");
                args.Add("yuv420p");
            }

            args.Add(blueprint.OutputDirectory);

            var psi = new ProcessStartInfo
            {
                FileName = EditSharpConfig.FfmpegPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
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
                    $"ffmpeg exited with code {process.ExitCode} finalizing output:\n{stderr}\n\n" +
                    $"Audio filter script preserved for inspection at: {scriptPath}");

            try { File.Delete(scriptPath); } catch { /* best-effort cleanup */ }
        }

        private static void Validate(Blueprint blueprint)
        {
            if (blueprint.Timeline == null || blueprint.Timeline.Channels.Count == 0)
                throw new ArgumentException("Blueprint.Timeline must contain at least one Channel.");

            if (blueprint.Timeline.Channels.All(c => c.Clips.Count == 0))
                throw new ArgumentException("Blueprint.Timeline contains no clips on any channel.");

            if (blueprint.Resolution.Item1 <= 0 || blueprint.Resolution.Item2 <= 0)
                throw new ArgumentException("Blueprint.Resolution must have positive width and height.");

            if (blueprint.Framerate <= 0)
                throw new ArgumentException("Blueprint.Framerate must be positive.");

            if (string.IsNullOrWhiteSpace(blueprint.OutputDirectory))
                throw new ArgumentException("Blueprint.OutputDirectory must be a full output file path.");

            //A transition that does not preserve alpha punches an opaque
            //rectangle through everything beneath it for the length of the
            //transition. Constants.PreservesAlpha(transition.Type) is gone
            //along with the old TransitionType enum (item 8) — replaced with
            //a direct type check, since FadeToColorTransition is now the
            //ONLY kind that's alpha-unsafe by construction (it deliberately
            //fills the whole canvas with a colour partway through). This is
            //a reasoned-from-construction judgement, not a re-measurement
            //the way the old AlphaUnsafeTransitions list was empirically
            //built — flagged as such in the migration manifest.
            foreach (Channel channel in blueprint.Timeline.Channels.Skip(1))
            {
                foreach ((Clip clip, Transition transition) in channel.Transitions)
                {
                    if (transition is not FadeToColorTransition) continue;

                    throw new ArgumentException(
                        $"Channel '{channel.Name}' uses a FadeToColorTransition, which does " +
                        "not preserve transparency, so it would black out the channels " +
                        "beneath it for the length of the transition. Use a different " +
                        "transition, or move this channel to the bottom of the timeline.");
                }
            }
        }
    }
}
