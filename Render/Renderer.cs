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
using EditSharp.Composite;

namespace EditSharp.Render
{
    /// <summary>
    /// Entry point for the render strategy — wires the Skia compositor into
    /// an actual end-to-end render.
    ///
    /// Shape of a render:
    ///   1. Probe every Video-type VideoSourceNode's native size (MediaProbe)
    ///      across the whole timeline — everything about a video clip's media
    ///      inputs that's constant across its whole life, done once up front
    ///      (RenderContentPreparation). TextInputNode/ColorGeneratorInputNode/
    ///      NoiseInputNode/TimelineVideoInputNode need no such up-front prep
    ///      any more — see RenderContentPreparation's own remarks.
    ///   2. Mix the timeline's audio once (AudioMixer.ComposeAsync) and spawn
    ///      the SINGLE ffmpeg process that will do the real mux + encode for
    ///      the whole render, with its stdin left open as a raw-video pipe.
    ///   3. Render every output frame SEQUENTIALLY directly against an
    ///      in-process SKCanvas (SkFrameCompositor), writing each frame's raw
    ///      RGBA8888 bytes straight into that ffmpeg process's stdin as it's
    ///      produced — see STREAMING REWRITE below. A Video-type
    ///      VideoSourceNode's SkSourceDecoder is opened on its clip's first
    ///      visible frame and disposed once that clip's visible window ends
    ///      (SkClipContentSource).
    ///   4. Close stdin once every frame has been written (ffmpeg's rawvideo
    ///      demuxer treats that exactly like reaching EOF on a file) and wait
    ///      for ffmpeg to finish encoding.
    ///
    /// STREAMING REWRITE — WHY THIS EXISTS: this used to append every frame's
    /// raw, uncompressed RGBA8888 bytes to a single growing "accumulator"
    /// temp file on disk, and only handed that file to ffmpeg for mux/encode
    /// once every frame had already been rendered. At 1920x1080 that's
    /// 8,294,400 bytes/frame (~7.9MiB); an 18-minute 30fps render is 32,400
    /// frames, i.e. roughly 268GB of raw bytes hitting disk before a single
    /// byte of that data was ever compressed — reported as "hundreds of
    /// gigabytes of writes" and confirmed to match this arithmetic almost
    /// exactly, not a leak or a runaway loop. Frame rendering now writes
    /// directly into the SAME ffmpeg process's stdin pipe that does the real
    /// mux/encode (mirroring how SkSourceDecoder already pipes raw frames IN
    /// from an ffmpeg decode process — this is the same pattern on the encode
    /// side), so raw video never touches disk at all: ffmpeg compresses each
    /// frame into the target codec as it arrives instead of after the whole
    /// timeline has already been written out losslessly. Disk usage for the
    /// video side of a render is now effectively just the size of the final
    /// encoded output file.
    ///
    /// AUDIO STILL GOES THROUGH A SMALL TEMP FILE, DELIBERATELY: the mixed
    /// master PCM (see AudioMixer.ComposeAsync) is orders of magnitude
    /// smaller than raw video (48kHz stereo f32 is 384,000 bytes/sec, so an
    /// 18-minute timeline is ~414MB, not hundreds of gigabytes) and ffmpeg
    /// needs it as a real, complete, seekable input at process-start time —
    /// unlike the video side, it isn't produced incrementally by anything
    /// this class does. A second OS pipe/named-pipe could avoid even that,
    /// but isn't pursued here: it would need real cross-platform machinery
    /// (named pipes on Windows, a FIFO on Unix) for a temp file that was
    /// never the actual disk-usage problem.
    ///
    /// ONE CONSEQUENCE OF STREAMING WORTH FLAGGING: because AudioMixer.
    /// ComposeAsync's result has to be written to that temp file and handed
    /// to ffmpeg as an -i argument BEFORE ffmpeg can be spawned — and frame
    /// rendering can't start streaming into ffmpeg's stdin until ffmpeg
    /// exists — audio composition is now awaited before frame rendering
    /// begins, rather than run concurrently with it the way this used to
    /// work. It still overlaps content preparation and GPU/decoder setup
    /// above it, which is normally the larger win of the two; audio mixing
    /// itself is a one-shot in-memory computation, not a per-frame cost, so
    /// this is expected to be a minor, not a proportional, regression versus
    /// however long the frame-by-frame render itself takes.
    ///
    /// "CLIPS ARE GRAPHS" REWRITE: RenderContentPreparation's
    /// nativeSizes/decodePlans/decodeSourcePaths dictionaries are now keyed
    /// by InputNode Id (Guid), not by Clip — a VideoClip's graph can contain
    /// more than one VideoSourceNode. staticImagePaths and the tempFiles bag
    /// PrepareContentAsync used to take are both gone from that call — text
    /// rasterization now happens lazily inside SkClipContentSource itself,
    /// which owns cleaning up its own temp files on Dispose (this file's own
    /// tempFiles bag is now used only for the render's OWN raw audio PCM temp
    /// file — the raw video accumulator described above is gone entirely,
    /// see the STREAMING REWRITE remarks). FrameStateResolver.Resolve no
    /// longer takes a nativeSizes parameter at all — see its own remarks.
    ///
    /// REWRITE ("channels split by kind"): the SkSurfacePool seed count below
    /// now uses timeline.VideoChannels.Count specifically (only a
    /// VideoChannel's clips ever need a GPU-backed canvas surface — see
    /// SkSurfacePool's own remarks on this being a warm-start heuristic, not
    /// a hard cap) instead of the old mixed timeline.Channels.Count. Validate's
    /// FadeToColorTransition check below also now walks timeline.VideoChannels
    /// specifically — that check is fundamentally about VIDEO channel
    /// stacking (an opaque transition blacking out whatever composites
    /// beneath it), which AudioChannels were never actually part of; walking
    /// VideoChannels directly says what the check means instead of relying on
    /// AudioChannel transitions happening to never trip it.
    /// </summary>
    public static class Renderer
    {
        //Standard, plenty for any of AudioCodec's targets (AAC/MP3/FLAC all
        //happily accept 48kHz stereo) — matches PlaybackAudioEngine's own
        //SampleRate/ChannelCount constants, so a render and a live preview of
        //the same timeline are mixed identically.
        private const int AudioSampleRate = 48000;
        private const int AudioChannelCount = 2;

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
            int width = (int)blueprint.RenderSettings.Resolution.X;
            int height = (int)blueprint.RenderSettings.Resolution.Y;
            int fps = blueprint.RenderSettings.Framerate;
            HardwareAccelerator hwAccel = blueprint.RenderSettings.HardwareAccelerator;

            var nativeSizes = new ConcurrentDictionary<Guid, (int, int)>();
            var decodePlans = new ConcurrentDictionary<Guid, DecodeHwAccelPlan>();
            var decodeSourcePaths = new ConcurrentDictionary<Guid, string>();

            var prepSw = Stopwatch.StartNew();
            await RenderContentPreparation.PrepareContentAsync(
                timeline, width, height, hwAccel, nativeSizes, decodePlans, decodeSourcePaths);
            EditSharpConfig.Logger.LogVerbose($"Content prepared in {prepSw.ElapsedMilliseconds}ms.");

            Dictionary<int, List<Clip>> decoderReleaseSchedule =
                RenderContentPreparation.BuildDecoderReleaseSchedule(timeline, fps);

            int totalFrames = Math.Max(1, (int)Math.Ceiling(timeline.Duration.TotalSeconds * fps));

            using var contentSource = new SkClipContentSource(
                fps, hwAccel, nativeSizes, decodePlans, decodeSourcePaths: decodeSourcePaths);

            using GpuContext gpuContext = GpuContext.Create(
                hwAccel, blueprint.RenderSettings.GpuAdapterIndex);
            using var surfacePool = new SkSurfacePool(
                gpuContext.GRContext, width, height, timeline.VideoChannels.Count);

            //Audio mixing touches no GPU/Skia state at all, so nothing stops
            //it running concurrently with the GPU/decoder setup above — but
            //it IS awaited here, before frame rendering starts, rather than
            //run concurrently with the frame loop the way this used to work.
            //See this class's STREAMING REWRITE remarks for why: the ffmpeg
            //process frame rendering streams into can't be spawned until the
            //mixed audio has already been written to a real, complete file
            //ffmpeg can open as an input.
            AudioBuffer masterAudio = await AudioMixer.ComposeAsync(timeline, AudioSampleRate, AudioChannelCount);

            string audioPath = GraphUtilities.GetAudioTempFilePath($"master_{Guid.NewGuid():N}.pcm");
            await File.WriteAllBytesAsync(audioPath, masterAudio.ToFloat32Bytes());
            tempFiles.Add(audioPath);

            EditSharpConfig.Logger.Log(
                $"Rendering {totalFrames} frame(s) at {width}x{height}@{fps}fps " +
                "(sequential, in-process Skia compositor, streamed directly into ffmpeg — " +
                "no raw video temp file).");

            var renderSw = Stopwatch.StartNew();
            await RenderAndEncodeAsync(
                timeline, fps, width, height, contentSource, decoderReleaseSchedule, totalFrames,
                surfacePool, audioPath, masterAudio, blueprint);
            EditSharpConfig.Logger.Log($"Render + encode complete in {renderSw.ElapsedMilliseconds}ms.");
        }

        /// <summary>
        /// Spawns the render's single ffmpeg mux/encode process up front, with
        /// its stdin left open as a raw-video pipe (`-i pipe:0`), then renders
        /// every output frame directly into that pipe as it's composited —
        /// see this class's STREAMING REWRITE remarks for why. ffmpeg
        /// compresses each frame into the target codec as it arrives rather
        /// than waiting for the whole timeline to be written out losslessly
        /// first, so raw video never touches disk.
        /// </summary>
        private static async Task RenderAndEncodeAsync(
            Timeline timeline, int fps, int width, int height,
            SkClipContentSource contentSource,
            Dictionary<int, List<Clip>> decoderReleaseSchedule,
            int totalFrames, SkSurfacePool surfacePool,
            string audioPath, AudioBuffer masterAudio, Blueprint blueprint)
        {
            bool isGif = blueprint.RenderSettings.VideoCodec == VideoCodec.GIF;
            (string videoEncoderName, List<string> videoQualityArgs) =
                await FfmpegRunner.GetVideoEncoderSettingsAsync(
                    blueprint.RenderSettings.VideoCodec, blueprint.RenderSettings.HardwareAccelerator);

            var args = new List<string> { "-y", "-v", "error" };
            args.AddRange(GraphUtilities.FilterThreadingArgs());

            args.AddRange(
            [
                "-f", "rawvideo",
                "-pix_fmt", SkOutputFormat.FfmpegPixelFormat,
                "-s", $"{width}x{height}",
                "-r", fps.ToString(CultureInfo.InvariantCulture),
                //STREAMED, NOT A TEMP FILE — this process's own stdin. Frame
                //rendering below writes directly into this pipe as each frame
                //is composited; ffmpeg's rawvideo demuxer just reads
                //sequentially off it exactly like it would a file, and
                //closing the pipe (below) is what tells it input has ended,
                //the same way reaching EOF on a file would.
                "-i", "pipe:0",
            ]);

            if (!isGif)
            {
                args.AddRange(
                [
                    "-f", "f32le",
                    "-ar", masterAudio.SampleRate.ToString(CultureInfo.InvariantCulture),
                    "-ac", masterAudio.Channels.ToString(CultureInfo.InvariantCulture),
                    "-i", audioPath,
                ]);
            }

            args.Add("-map");
            args.Add("0:v");

            if (!isGif)
            {
                args.Add("-map");
                args.Add("1:a");
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

                string audioCodecName = Constants.AudioCodecNames[blueprint.RenderSettings.AudioCodec];
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
                RedirectStandardInput = true,
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

            Stream stdin = process.StandardInput.BaseStream;
            try
            {
                await RenderAllFramesAsync(
                    timeline, fps, width, height, contentSource,
                    decoderReleaseSchedule, totalFrames, stdin, surfacePool);
            }
            finally
            {
                //Always close stdin, even if frame rendering threw —
                //otherwise ffmpeg blocks forever waiting for more input that
                //will never arrive, and the process (and this render) hangs
                //instead of surfacing the real exception.
                stdin.Close();
            }

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"ffmpeg exited with code {process.ExitCode} rendering output:\n{stderr}");
        }

        private static async Task RenderAllFramesAsync(
            Timeline timeline, int fps, int width, int height,
            SkClipContentSource contentSource,
            Dictionary<int, List<Clip>> decoderReleaseSchedule,
            int totalFrames, Stream accumulator, SkSurfacePool surfacePool)
        {
            var sw = Stopwatch.StartNew();

            long previousElapsedMs = 0;

            for (int frameIndex = 0; frameIndex < totalFrames; frameIndex++)
            {
                FrameState state = FrameStateResolver.Resolve(timeline, frameIndex, fps);

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

                EditSharpConfig.Logger.LogVerbose(
                    $"Rendered frame {frameIndex + 1}/{totalFrames} " +
                    $"({frameDeltaMs}ms this frame, {currentElapsedMs}ms elapsed).");
            }
        }

        private static void Validate(Blueprint blueprint)
        {
            if (blueprint.Timeline == null || blueprint.Timeline.Channels.Count == 0)
                throw new ArgumentException("Blueprint.Timeline must contain at least one Channel.");

            if (blueprint.Timeline.Channels.All(c => c.Clips.Count == 0))
                throw new ArgumentException("Blueprint.Timeline contains no clips on any channel.");

            if ((int)blueprint.RenderSettings.Resolution.X <= 0 || (int)blueprint.RenderSettings.Resolution.Y <= 0)
                throw new ArgumentException("Blueprint.Resolution must have positive width and height.");

            if (blueprint.RenderSettings.Framerate <= 0)
                throw new ArgumentException("Blueprint.Framerate must be positive.");

            if (blueprint.RenderSettings.GpuAdapterIndex is < 0)
                throw new ArgumentException(
                    "Blueprint.RenderSettings.GpuAdapterIndex must be null (auto) or a non-negative " +
                    "DXGI adapter index. Valid indices for this machine are listed in the log at the " +
                    "start of every GPU session.");

            if (string.IsNullOrWhiteSpace(blueprint.OutputDirectory))
                throw new ArgumentException("Blueprint.OutputDirectory must be a full output file path.");

            //A transition that does not preserve alpha punches an opaque
            //rectangle through everything beneath it for the length of the
            //transition. FadeToColorTransition is the only kind that's
            //alpha-unsafe by construction (it deliberately fills the whole
            //canvas with a colour partway through) — a direct type check,
            //reasoned from construction rather than re-measured. Walks
            //VideoChannels specifically (not the mixed Channels view) — this
            //is a video-compositing concern, about what draws on top of what;
            //see this class's own remarks.
            foreach (Channel channel in blueprint.Timeline.VideoChannels.Skip(1))
            {
                foreach (Transition transition in channel.Transitions)
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