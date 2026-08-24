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
    ///   1. Probe every Video-type MediaSourceNode's native size (MediaProbe)
    ///      across the whole timeline — everything about a video clip's media
    ///      inputs that's constant across its whole life, done once up front
    ///      (RenderContentPreparation). TextInputNode/ColorGeneratorInputNode/
    ///      NoiseInputNode/TimelineVideoInputNode need no such up-front prep
    ///      any more — see RenderContentPreparation's own remarks.
    ///   2. Render every output frame SEQUENTIALLY directly against an
    ///      in-process SKCanvas (SkFrameCompositor), appending each frame's
    ///      raw RGBA8888 bytes to a single growing lossless accumulator
    ///      file. A Video-type MediaSourceNode's SkSourceDecoder is opened on
    ///      its clip's first visible frame and disposed once that clip's
    ///      visible window ends (SkClipContentSource).
    ///   3. Mux that accumulated video against the timeline's audio
    ///      (FinalizeOutputAsync) and encode to Blueprint's chosen codec.
    ///
    /// "CLIPS ARE GRAPHS" REWRITE: RenderContentPreparation's
    /// nativeSizes/decodePlans/decodeSourcePaths dictionaries are now keyed
    /// by InputNode Id (Guid), not by Clip — a VideoClip's graph can contain
    /// more than one MediaSourceNode. staticImagePaths and the tempFiles bag
    /// PrepareContentAsync used to take are both gone from that call — text
    /// rasterization now happens lazily inside SkClipContentSource itself,
    /// which owns cleaning up its own temp files on Dispose (this file's own
    /// tempFiles bag is still used for the render's OWN temp files — the raw
    /// video accumulator and the raw audio PCM — just no longer shared with
    /// content preparation). FrameStateResolver.Resolve no longer takes a
    /// nativeSizes parameter at all — see its own remarks.
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
 
            string accumulatorPath = GraphUtilities.GetVideoTempFilePath($"frames_{Guid.NewGuid():N}.raw");
            tempFiles.Add(accumulatorPath);
 
            EditSharpConfig.Logger.Log(
                $"Rendering {totalFrames} frame(s) at {width}x{height}@{fps}fps " +
                "(sequential, in-process Skia compositor).");
 
            using var contentSource = new SkClipContentSource(
                fps, hwAccel, nativeSizes, decodePlans, decodeSourcePaths: decodeSourcePaths);
 
            using GpuContext gpuContext = GpuContext.Create(
                hwAccel, blueprint.RenderSettings.GpuAdapterIndex);
            using var surfacePool = new SkSurfacePool(
                gpuContext.GRContext, width, height, timeline.Channels.Count);
 
            //audio evaluation touches no GPU/Skia state at all, so it's kicked
            //off concurrently with frame rendering rather than after it —
            //independent work, no reason to serialize the two
            Task<AudioBuffer> audioTask = AudioMixer.ComposeAsync(timeline, AudioSampleRate, AudioChannelCount);
 
            using (var accumulator = new FileStream(
                accumulatorPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 1 << 20))
            {
                await RenderAllFramesAsync(
                    timeline, fps, width, height, contentSource,
                    decoderReleaseSchedule, totalFrames, accumulator, surfacePool);
            }
 
            AudioBuffer masterAudio = await audioTask;
 
            EditSharpConfig.Logger.Log("Finalizing output (mux + encode)...");
            var finalizeSw = Stopwatch.StartNew();
            await FinalizeOutputAsync(accumulatorPath, masterAudio, width, height, fps, blueprint, tempFiles);
            EditSharpConfig.Logger.Log($"Finalize complete in {finalizeSw.ElapsedMilliseconds}ms.");
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
 
        /// <summary>
        /// Muxes the accumulated lossless video against the timeline's
        /// already fully-mixed, already graph-evaluated master AudioBuffer
        /// and encodes to Blueprint's chosen codec. No filter_complex is
        /// built for audio at all any more — the buffer is written to a raw
        /// f32le temp file and mapped as a second plain input, since all the
        /// real mixing/effects work already happened in AudioMixer.ComposeAsync.
        /// </summary>
        private static async Task FinalizeOutputAsync(
            string accumulatorPath, AudioBuffer masterAudio, int width, int height, int fps,
            Blueprint blueprint, ConcurrentBag<string> tempFiles)
        {
            string audioPath = GraphUtilities.GetAudioTempFilePath($"master_{Guid.NewGuid():N}.pcm");
            await File.WriteAllBytesAsync(audioPath, masterAudio.ToFloat32Bytes());
            tempFiles.Add(audioPath);
 
            bool isGif = blueprint.RenderSettings.VideoCodec == VideoCodec.GIF;
            (string videoEncoderName, List<string> videoQualityArgs) =
                await FfmpegRunner.GetVideoEncoderSettingsAsync(
                    blueprint.RenderSettings.VideoCodec, blueprint.RenderSettings.HardwareAccelerator);
 
            var args = new List<string> { "-y", "-v", "error" };
            args.AddRange(GraphUtilities.FilterThreadingArgs());
 
            args.AddRange(new[]
            {
                "-f", "rawvideo",
                "-pix_fmt", SkOutputFormat.FfmpegPixelFormat,
                "-s", $"{width}x{height}",
                "-r", fps.ToString(CultureInfo.InvariantCulture),
                "-i", accumulatorPath,
            });
 
            if (!isGif)
            {
                args.AddRange(new[]
                {
                    "-f", "f32le",
                    "-ar", masterAudio.SampleRate.ToString(CultureInfo.InvariantCulture),
                    "-ac", masterAudio.Channels.ToString(CultureInfo.InvariantCulture),
                    "-i", audioPath,
                });
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
                    $"ffmpeg exited with code {process.ExitCode} finalizing output:\n{stderr}");
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
            //reasoned from construction rather than re-measured.
            foreach (Channel channel in blueprint.Timeline.Channels.Skip(1))
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
 