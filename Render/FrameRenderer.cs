using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EditSharp;
using EditSharp.Components;
using EditSharp.Components.Clips;

namespace EditSharp.Render
{
    /// <summary>
    /// Entry point for the frame-by-frame render strategy — this pipeline's only
    /// render path (the old single-filter_complex-per-window approach and its
    /// supporting classes have been removed rather than kept alongside this one).
    ///
    /// Shape of a render:
    ///   1. Build optimized media for every video source clip, upfront, with
    ///      bounded concurrency (OptimizedMediaBuilder).
    ///   2. Prepare everything else that's static across a clip's whole life —
    ///      an Image source's dimensions, a TextClip's ONE rasterized PNG —
    ///      so the per-frame loop never repeats work a clip's own content
    ///      doesn't actually vary frame to frame.
    ///   3. Render every output frame, one ffmpeg process each, appending each
    ///      frame's raw rgba64le bytes to a single growing lossless file.
    ///      Optimized media is deleted the moment no remaining frame needs it.
    ///   4. Mux that lossless video against the timeline's audio (see
    ///      FinalizeOutputAsync) and encode to Blueprint's chosen codec.
    /// </summary>
    public static class FrameRenderer
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
            IFrameFilterChainBuilder chainBuilder = SelectChainBuilder(blueprint.HardwareAccelerator);

            Timeline timeline = blueprint.Timeline;
            int width = blueprint.Resolution.Item1;
            int height = blueprint.Resolution.Item2;
            int fps = blueprint.Framerate;

            Dictionary<Clip, OptimizedMediaBuilder.OptimizedMedia> optimizedMedia =
                await OptimizedMediaBuilder.BuildAsync(
                    timeline, fps, width, height, tempFiles, blueprint.ExtractionConcurrency);

            foreach (OptimizedMediaBuilder.OptimizedMedia media in optimizedMedia.Values)
                tempFiles.Add(media.Path);

            var nativeSizes = new Dictionary<Clip, (int, int)>();
            var staticImages = new Dictionary<Clip, string>();

            //native size for every VIDEO clip comes straight from the probe
            //OptimizedMediaBuilder already did — no second ffprobe call for
            //what's already known
            foreach ((Clip clip, OptimizedMediaBuilder.OptimizedMedia media) in optimizedMedia)
                nativeSizes[clip] = (media.NativeWidth, media.NativeHeight);

            var staticSw = Stopwatch.StartNew();
            await PrepareStaticContentAsync(
                timeline, width, height, nativeSizes, staticImages, tempFiles);
            EditSharpConfig.Logger.LogVerbose($"Static content prepared in {staticSw.ElapsedMilliseconds}ms.");

            //precomputed once, up front — every clip's Start/Duration/End is
            //already known, so there is nothing to discover at render time.
            //Grouped by frame index rather than scanned per clip per frame: an
            //O(1) dictionary lookup per frame instead of an O(clips) scan
            Dictionary<int, List<string>> deletionSchedule = optimizedMedia.Values
                .GroupBy(m => m.DeleteAfterFrame)
                .ToDictionary(g => g.Key, g => g.Select(m => m.Path).ToList());

            int totalFrames = Math.Max(1, (int)Math.Ceiling(timeline.Duration.TotalSeconds * fps));

            string accumulatorPath = GraphUtilities.GetVideoTempFilePath($"frames_{Guid.NewGuid():N}.raw");
            tempFiles.Add(accumulatorPath);

            EditSharpConfig.Logger.Log(
                $"Rendering {totalFrames} frame(s) at {width}x{height}@{fps}fps " +
                $"(concurrency {blueprint.FrameRenderConcurrency}).");

            using (var accumulator = new FileStream(
                accumulatorPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 1 << 20))
            {
                await RenderAllFramesAsync(
                    timeline, fps, width, height, chainBuilder, optimizedMedia, nativeSizes,
                    staticImages, deletionSchedule, totalFrames,
                    blueprint.FrameRenderConcurrency, accumulator, tempFiles);
            }

            EditSharpConfig.Logger.Log("Finalizing output (mux + encode)...");
            var finalizeSw = Stopwatch.StartNew();
            await FinalizeOutputAsync(accumulatorPath, width, height, fps, blueprint, tempFiles);
            EditSharpConfig.Logger.Log($"Finalize complete in {finalizeSw.ElapsedMilliseconds}ms.");
        }

        /// <summary>
        /// Renders every output frame, up to FrameRenderConcurrency at once, and
        /// writes each one to the accumulator STRICTLY in order.
        ///
        /// Every frame's task is launched immediately; the SemaphoreSlim inside
        /// RenderOneAsync is what actually bounds how many are mid-render at
        /// once, not this method. Frames can finish rendering out of order once
        /// more than one is in flight, but two things both require frame i to
        /// be fully settled before frame i+1 is acted on: the accumulator is a
        /// headerless raw stream with no per-frame timestamps, so writes must
        /// land in order; and a clip's optimized media must not be deleted
        /// until every frame that could still need it — up to and including its
        /// DeleteAfterFrame — has actually finished rendering, not merely been
        /// scheduled. Awaiting frameTasks[frameIndex] in a plain increasing loop
        /// satisfies both for free, and — just as importantly — surfaces a
        /// fault on any frame the moment that frame's turn comes up. An earlier
        /// version of this method buffered completions into a side dictionary
        /// and only reached its final Task.WhenAll after every frame had been
        /// LAUNCHED, which meant a frame 0 exception sat unobserved for the
        /// entire render and looked identical to a hang.
        /// </summary>
        private static async Task RenderAllFramesAsync(
            Timeline timeline, int fps, int width, int height,
            IFrameFilterChainBuilder chainBuilder,
            Dictionary<Clip, OptimizedMediaBuilder.OptimizedMedia> optimizedMedia,
            Dictionary<Clip, (int, int)> nativeSizes,
            Dictionary<Clip, string> staticImages,
            Dictionary<int, List<string>> deletionSchedule,
            int totalFrames, int concurrency,
            Stream accumulator, ConcurrentBag<string> tempFiles)
        {
            using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
            var frameTasks = new Task<byte[]>[totalFrames];
            var sw = Stopwatch.StartNew();

            //frame 0 alone can't tell us whether per-frame seek cost GROWS
            //with how far into the source the target timestamp is — its own
            //seek offset is always near zero. A second checkpoint partway
            //through gives ffmpeg's own decode_video bench numbers something
            //to compare against: if seeking without a usable Cues index falls
            //back to an O(n) forward scan, the SAME clips' decode_video "real"
            //time at this checkpoint should be visibly larger than at frame 0.
            //100 if the render is that long, otherwise roughly the midpoint —
            //either way, meaningfully further into the source than frame 0.
            int benchmarkCheckpoint = Math.Min(100, totalFrames / 2);

            async Task<byte[]> RenderOneAsync(int frameIndex)
            {
                await gate.WaitAsync();
                try
                {
                    //logged the moment this frame actually gets a gate slot and
                    //begins building its graph — if a render appears stuck, this
                    //line (or its absence) is what tells you whether it's stuck
                    //BEFORE ffmpeg is even spawned (graph construction) or DURING
                    //ffmpeg's own run (no further logs after this one)
                    EditSharpConfig.Logger.LogVerbose(
                        $"Starting frame {frameIndex + 1}/{totalFrames}...");

                    FrameState state = FrameStateResolver.Resolve(
                        timeline, frameIndex, fps, optimizedMedia, nativeSizes, staticImages);

                    bool benchmark = frameIndex == 0 || frameIndex == benchmarkCheckpoint;

                    return await RenderFrameAsync(
                        state, chainBuilder, width, height, fps, tempFiles, benchmark);
                }
                finally
                {
                    gate.Release();
                }
            }

            //every frame's task is launched up front — the gate above, not this
            //loop, is what bounds how many are actually mid-render at once, so
            //launching them all costs nothing but Task allocation for the ones
            //still queued on the gate
            for (int frameIndex = 0; frameIndex < totalFrames; frameIndex++)
                frameTasks[frameIndex] = RenderOneAsync(frameIndex);

            for (int frameIndex = 0; frameIndex < totalFrames; frameIndex++)
            {
                //awaited strictly in order. This is what the accumulator's
                //no-timestamps format requires regardless, but it also means a
                //fault on any frame surfaces the moment that frame's turn comes
                //up — NOT buried until every one of totalFrames tasks has been
                //launched and Task.WhenAll is finally reached, which is what an
                //earlier version of this method did and which silently hid a
                //frame 0 failure behind what looked like a hang
                byte[] data = await frameTasks[frameIndex];

                await accumulator.WriteAsync(data);

                EditSharpConfig.Logger.LogVerbose(
                    $"Rendered frame {frameIndex + 1}/{totalFrames} " +
                    $"({sw.ElapsedMilliseconds}ms elapsed).");

                if (deletionSchedule.TryGetValue(frameIndex, out List<string>? exhausted))
                {
                    foreach (string path in exhausted)
                    {
                        try { File.Delete(path); } catch { /* best-effort */ }
                    }
                }
            }
        }

        /// <summary>
        /// Picks the filter-chain construction strategy for Blueprint.
        /// HardwareAccelerator. Only None (software, the stock CPU filter
        /// chain) is implemented — the GPU/libplacebo chain is a paused,
        /// separate effort (see the project notes) and is deliberately NOT
        /// silently downgraded to software the way the old whole-window
        /// pipeline downgrades an unavailable NVENC encoder: a render that
        /// asked for hardware and got software instead, with no error, is
        /// exactly the kind of silent behaviour change worth failing loudly on
        /// instead.
        /// </summary>
        private static IFrameFilterChainBuilder SelectChainBuilder(HardwareAccelerator accelerator) =>
            accelerator switch
            {
                HardwareAccelerator.None => new SoftwareFrameFilterChainBuilder(),
                HardwareAccelerator.Nvenc => throw new NotImplementedException(
                    "Frame-by-frame rendering with HardwareAccelerator.Nvenc (the GPU/libplacebo " +
                    "filter chain) is not implemented yet. Use HardwareAccelerator.None."),
                _ => throw new NotSupportedException($"Unknown HardwareAccelerator value: {accelerator}."),
            };

        /// <summary>
        /// Everything about a clip that's constant across its whole life and
        /// would otherwise be redone on every frame it's visible on: an Image
        /// source's dimensions, and a TextClip's rasterized PNG (built exactly
        /// ONCE here rather than once per frame — Content never changes mid-clip,
        /// so re-rasterizing identical text for every visible frame would be
        /// pure waste).
        ///
        /// SourceClip video is deliberately absent — its native size comes from
        /// OptimizedMediaBuilder's own probe, and GeneratorClip/NoiseClip need
        /// no entry at all since SoftwareFrameFilterChainBuilder sizes them to
        /// the canvas directly.
        /// </summary>
        private static Task PrepareStaticContentAsync(
            Timeline timeline, int canvasWidth, int canvasHeight,
            Dictionary<Clip, (int, int)> nativeSizes,
            Dictionary<Clip, string> staticImages,
            ConcurrentBag<string> tempFiles)
        {
            var tasks = new List<Task>();

            foreach (Channel channel in timeline.Channels)
            {
                foreach (Clip clip in channel.Clips.Values)
                {
                    switch (clip)
                    {
                        case SourceClip { Source.Type: SourceType.Image } imageClip:
                            tasks.Add(PrepareImageAsync(clip, imageClip, nativeSizes, staticImages));
                            break;

                        case TextClip text:
                            PrepareText(clip, text, canvasWidth, canvasHeight,
                                nativeSizes, staticImages, tempFiles);
                            break;
                    }
                }
            }

            return Task.WhenAll(tasks);
        }

        private static async Task PrepareImageAsync(
            Clip clip, SourceClip imageClip,
            Dictionary<Clip, (int, int)> nativeSizes, Dictionary<Clip, string> staticImages)
        {
            (int width, int height) = await MediaProbe.GetDimensionsAsync(imageClip.Source.Path);
            nativeSizes[clip] = (width, height);
            staticImages[clip] = imageClip.Source.Path;
        }

        private static void PrepareText(
            Clip clip, TextClip text, int canvasWidth, int canvasHeight,
            Dictionary<Clip, (int, int)> nativeSizes, Dictionary<Clip, string> staticImages,
            ConcurrentBag<string> tempFiles)
        {
            string path = TextRasterizer.Rasterize(
                text, canvasWidth, canvasHeight, out int width, out int height);

            tempFiles.Add(path);
            nativeSizes[clip] = (width, height);
            staticImages[clip] = path;
        }

        /// <summary>
        /// Renders one output frame: builds its filter graph and runs ffmpeg
        /// with -frames:v 1 writing raw rgba64le to stdout, buffered in memory
        /// and handed back to the caller rather than written straight to a
        /// shared accumulator stream — RenderAllFramesAsync may have several of
        /// these in flight at once, and a FileStream can't be written by
        /// multiple callers concurrently. The caller is responsible for
        /// flushing the bytes to the accumulator in frame order.
        ///
        /// The filter_complex is passed inline rather than through the
        /// "-/filter_complex &lt;file&gt;" mechanism RunFfmpegAsync uses for the
        /// whole-window pipeline. A per-frame graph is bounded by how many
        /// clips can be simultaneously visible (at most a handful per channel,
        /// two mid-transition), nothing like the size a whole timeline's graph
        /// reaches — and avoiding a temp file per frame matters here, since
        /// this runs once per output frame rather than once per render. If a
        /// project ever produces per-frame graphs large enough to hit the OS
        /// command-line limit, switch this call site to the same script-file
        /// trick.
        /// </summary>
        private static async Task<byte[]> RenderFrameAsync(
            FrameState state, IFrameFilterChainBuilder chainBuilder,
            int width, int height, int fps,
            ConcurrentBag<string> tempFiles, bool benchmark)
        {
            var stageSw = Stopwatch.StartNew();

            var graph = new InputGraph();
            string finalLabel = chainBuilder.Build(state, graph, width, height, fps, tempFiles);

            foreach (var input in graph.Inputs)
            {
                if (input.VerifyExists && !File.Exists(input.Path))
                    throw new FileNotFoundException(
                        $"Input file not found rendering frame {state.FrameIndex}: {input.Path}",
                        input.Path);
            }

            long graphBuildMs = stageSw.ElapsedMilliseconds;
            stageSw.Restart();

            //runs with ffmpeg's own -benchmark_all so it prints a decode/
            //encode/flush timing breakdown to stderr, instead of the manual-
            //command approach (which needs the filter_complex string
            //re-quoted for a shell and evidently doesn't survive that
            //intact). -v info rather than error is required for
            //-benchmark_all's output to actually appear. The caller decides
            //which frame indices this fires on (see RenderAllFramesAsync) —
            //frame 0 plus a later checkpoint, so decode_video's "real" time
            //for the SAME clips can be compared at a near-zero seek offset
            //against a much larger one, to test whether seek cost grows with
            //how far into the source the target timestamp is.

            var args = benchmark
                ? new List<string> { "-y", "-v", "info", "-benchmark_all" }
                : new List<string> { "-y", "-v", "error" };

            foreach (var input in graph.Inputs)
            {
                if (input.ExtraArgs != null) args.AddRange(input.ExtraArgs);
                args.Add("-i");
                args.Add(input.Path);
            }

            args.Add("-filter_complex");
            args.Add(string.Join(";", graph.FilterLines));

            args.Add("-map");
            args.Add($"[{finalLabel}]");

            args.Add("-frames:v");
            args.Add("1");
            args.Add("-f");
            args.Add("rawvideo");
            args.Add("-pix_fmt");
            args.Add(PixelFormats.Primary);
            args.Add("pipe:1");

            var psi = new ProcessStartInfo
            {
                FileName = EditSharpConfig.FfmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi };
            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            //timed separately from graph build above and from CopyToAsync below
            //so a slow frame can be attributed to one of three distinct causes:
            //.NET building the args/graph, the OS actually getting the process
            //running (process.Start() returning), or ffmpeg's own filter
            //execution (everything from Start() to the stdout copy finishing)
            var spawnSw = Stopwatch.StartNew();
            process.Start();
            long spawnMs = spawnSw.ElapsedMilliseconds;

            process.BeginErrorReadLine();

            //drained concurrently with stderr rather than read after exit —
            //the same deadlock risk MediaProbe's own comments describe: a full
            //pipe buffer blocks the child if nothing is consuming the other one
            var runSw = Stopwatch.StartNew();
            using var stdout = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(stdout);
            await process.WaitForExitAsync();
            long runMs = runSw.ElapsedMilliseconds;

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"ffmpeg exited with code {process.ExitCode} rendering frame " +
                    $"{state.FrameIndex}:\n{stderr}");

            //on the plain -v error frames stderr is normally empty and silently
            //discarded — but this run asked ffmpeg for -benchmark_all, and that
            //output only exists in stderr, so it has to be surfaced here or the
            //whole point of running with it is lost
            if (benchmark)
            {
                EditSharpConfig.Logger.Log(
                    $"Frame {state.FrameIndex} ffmpeg -benchmark_all output " +
                    $"(decode/encode/flush timing):\n{stderr}");
            }

            EditSharpConfig.Logger.LogVerbose(
                $"Frame {state.FrameIndex + 1}: graph build {graphBuildMs}ms, " +
                $"process spawn {spawnMs}ms, ffmpeg run {runMs}ms " +
                $"({graph.Inputs.Count} input(s), {graph.FilterLines.Count} filter line(s)).");

            return stdout.ToArray();
        }

        /// <summary>
        /// Muxes the accumulated lossless video against the timeline's audio
        /// and encodes to Blueprint's chosen codec — the one lossy step in the
        /// whole pipeline, and only when the codec itself is lossy.
        ///
        /// Audio is built the same way it always has in this pipeline: a fresh
        /// InputGraph, ClipContentBuilder per clip, AudioMixer.Compose — none of
        /// that changed when the video side moved to frame-by-frame. Worth
        /// knowing: ClipContentBuilder builds each
        /// clip's VIDEO label too, even though only the audio one is used here
        /// — those filter lines are simply never mapped to output, which
        /// ffmpeg tolerates, but it does mean a wasted decode per video clip
        /// during this pass. Not fixed here; would need an audio-only mode on
        /// ClipContentBuilder to avoid.
        /// </summary>
        private static async Task FinalizeOutputAsync(
            string accumulatorPath, int width, int height, int fps,
            Blueprint blueprint, ConcurrentBag<string> tempFiles)
        {
            var audioGraph = new InputGraph();
            var contents = new Dictionary<Clip, ClipContent>();

            foreach (Channel channel in blueprint.Timeline.Channels)
            {
                foreach (Clip clip in channel.Clips.Values)
                {
                    if (contents.ContainsKey(clip)) continue;

                    contents[clip] = await ClipContentBuilder.BuildAsync(
                        clip, audioGraph, width, height, fps, tempFiles);
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

            //input 0: the accumulated frames. Headerless raw data, so every
            //dimension ffmpeg would normally read from a container header has
            //to be told explicitly instead
            args.AddRange(new[]
            {
                "-f", "rawvideo",
                "-pix_fmt", PixelFormats.Primary,
                "-s", $"{width}x{height}",
                "-r", fps.ToString(CultureInfo.InvariantCulture),
                "-i", accumulatorPath,
            });

            //the audio graph's own inputs land at indices 1..N
            foreach (var input in audioGraph.Inputs)
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

            // A transition that does not preserve alpha punches an opaque rectangle
            // through everything beneath it for the length of the transition —
            // FrameFilterChain.ApplyTransition drives the same xfade transition
            // types this checks against, so the failure mode is identical to the
            // old whole-window pipeline's. Harmless on the bottom channel, which is
            // flattened onto black anyway.
            foreach (Channel channel in blueprint.Timeline.Channels.Skip(1))
            {
                foreach ((Clip clip, Transition transition) in channel.Transitions)
                {
                    if (Constants.PreservesAlpha(transition.Type)) continue;

                    throw new ArgumentException(
                        $"Channel '{channel.Name}' uses transition {transition.Type}, which does not " +
                        $"preserve transparency, so it would black out the channels beneath it for " +
                        $"the length of the transition. Use one of the alpha-safe transitions, or " +
                        $"move this channel to the bottom of the timeline.");
                }
            }
        }
    }
}
