using EditSharp.Components.Media;
using System;
using EditSharp.Audio.Engine;
using System.Threading;
using System.Runtime.InteropServices;
using System.IO.Pipes;
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
using EditSharp.Audio;
using EditSharp.Components;
using EditSharp.Components.Channels;
using EditSharp.Components.Clips;
using EditSharp.Components.Transitions;
using EditSharp.Compositing;
using EditSharp.Compositing.Gpu;
using EditSharp.Compositing.Sources;
using EditSharp.Video;

namespace EditSharp.Rendering
{
    /// <summary>Renders a timeline to a video file.</summary>
    /// <remarks>
    /// One ffmpeg process encodes the whole render. Frames are composited in
    /// order on the GPU and written straight into ffmpeg's stdin as raw RGBA;
    /// audio is rendered by the streaming audio engine at the same time and
    /// written into a named pipe ffmpeg reads as its second input. Neither ever
    /// touches disk before it's encoded. Sources are read ahead of the frame
    /// being composited, and each clip's decoder is closed once the clip has
    /// passed.
    /// </remarks>
    public static class Renderer
    {
        //48 kHz stereo, the same format live playback mixes in
        private const int AudioSampleRate = 48000;
        private const int AudioChannelCount = 2;

        /// <summary>Renders <paramref name="blueprint"/> to its output file.</summary>
        /// <remarks>A source that can't provide content doesn't stop the render: it's drawn as a labelled placeholder (or plays as silence), and the report says which sources failed, why, and where.</remarks>
        /// <param name="blueprint">What to render, how, and where.</param>
        /// <returns>The sources that failed, if any.</returns>
        /// <exception cref="ArgumentException">The blueprint is invalid: an empty timeline, a non-positive size or frame rate, a negative GPU index, no output path, or a fade-to-colour transition above the bottom channel.</exception>
        /// <exception cref="InvalidOperationException">ffmpeg failed, or streaming the audio failed.</exception>
        public static async Task<RenderReport> RenderAsync(Blueprint blueprint)
        {
            Validate(blueprint);

            var tempFiles = new ConcurrentBag<string>();
            var sw = Stopwatch.StartNew();

            EditSharpConfig.Logger.Log("Starting render.");

            try
            {
                RenderReport report = await RenderCoreAsync(blueprint, tempFiles);
                EditSharpConfig.Logger.Log(report.HasProblems
                    ? $"Render complete in {sw.Elapsed}, with {report.Problems.Count} source problem(s) rendered as placeholders."
                    : $"Render complete in {sw.Elapsed}.");
                return report;
            }
            finally
            {
                foreach (string path in tempFiles)
                {
                    try { File.Delete(path); } catch { /* best-effort cleanup */ }
                }
            }
        }

        private static async Task<RenderReport> RenderCoreAsync(Blueprint blueprint, ConcurrentBag<string> tempFiles)
        {
            Timeline timeline = blueprint.Timeline;
            int width = (int)blueprint.RenderSettings.Resolution.X;
            int height = (int)blueprint.RenderSettings.Resolution.Y;
            int fps = blueprint.RenderSettings.Framerate;
            HardwareAccelerator hwAccel = blueprint.RenderSettings.HardwareAccelerator;

            var report = new RenderReportBuilder();

            int totalFrames = Math.Max(1, (int)Math.Ceiling(timeline.Duration.TotalSeconds * fps));

            //read ahead of the frame being composed, from originals unless the settings say otherwise
            using var contentSource = new ClipContentSource(new ContentSourceOptions(
                fps, width, height, hwAccel, blueprint.RenderSettings.SourceMode,
                VideoReadMode.Sequential, ContentFailurePolicy.Export, Buffered: true, Direction: 1, Report: report));

            var prepSw = Stopwatch.StartNew();
            await contentSource.PrepareAsync(FrameStateResolver.Resolve(timeline, 0, fps));
            EditSharpConfig.Logger.LogVerbose($"Opening sources prepared in {prepSw.ElapsedMilliseconds}ms.");

            using GpuContext gpuContext = GpuContext.Create(
                hwAccel, blueprint.RenderSettings.GpuAdapterIndex);
            using var surfacePool = new SurfacePool(
                gpuContext.GRContext, width, height, timeline.VideoChannels.Count);

            EditSharpConfig.Logger.Log(
                $"Rendering {totalFrames} frame(s) at {width}x{height}@{fps}fps " +
                "(sequential, in-process Skia compositor, streamed straight into ffmpeg).");

            var renderSw = Stopwatch.StartNew();
            await RenderAndEncodeAsync(
                timeline, fps, width, height, contentSource, totalFrames,
                surfacePool, report, blueprint);
            EditSharpConfig.Logger.Log($"Render + encode complete in {renderSw.ElapsedMilliseconds}ms.");

            return report.Build();
        }

        //starts the one ffmpeg process with stdin open for raw frames (and a named pipe for audio), then feeds both
        private static async Task RenderAndEncodeAsync(
            Timeline timeline, int fps, int width, int height,
            ClipContentSource contentSource,
            int totalFrames, SurfacePool surfacePool,
            RenderReportBuilder report, Blueprint blueprint)
        {
            bool isGif = blueprint.RenderSettings.VideoCodec == VideoCodec.GIF;

            //audio streams in alongside the frames through a named pipe; GIFs have none
            string pipeName = $"editsharp-audio-{Guid.NewGuid():N}";
            using NamedPipeServerStream? audioPipe = isGif
                ? null
                : new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            (string videoEncoderName, List<string> videoQualityArgs) =
                await FfmpegRunner.GetVideoEncoderSettingsAsync(
                    blueprint.RenderSettings.VideoCodec, blueprint.RenderSettings.HardwareAccelerator);

            var args = new List<string> { "-y", "-v", "error" };
            args.AddRange(FfmpegArgs.FilterThreadingArgs());

            args.AddRange(
            [
                "-f", "rawvideo",
                "-pix_fmt", OutputFormat.FfmpegPixelFormat,
                "-s", $"{width}x{height}",
                "-r", fps.ToString(CultureInfo.InvariantCulture),
                //frames arrive on stdin; closing it ends the input like EOF on a file
                "-i", "pipe:0",
            ]);

            if (!isGif)
            {
                args.AddRange(
                [
                    "-f", "f32le",
                    "-ar", AudioSampleRate.ToString(CultureInfo.InvariantCulture),
                    "-ac", AudioChannelCount.ToString(CultureInfo.InvariantCulture),
                    "-i", $@"\\.\pipe\{pipeName}",
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

                string audioCodecName = CodecNames.AudioCodecNames[blueprint.RenderSettings.AudioCodec];
                args.Add("-c:a");
                args.Add(audioCodecName);
                args.Add("-b:a");
                args.Add("192k");
                args.Add("-shortest");
                args.Add("-pix_fmt");
                args.Add("yuv420p");
            }

            args.Add(blueprint.OutputPath);

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

            //if ffmpeg dies before it opens the audio pipe, stop waiting for it to
            using var exited = new CancellationTokenSource();
            process.Exited += (_, _) => { try { exited.Cancel(); } catch (ObjectDisposedException) { } };

            Task audio = audioPipe is null
                ? Task.CompletedTask
                : StreamAudioAsync(timeline, audioPipe, report, exited.Token);

            Stream stdin = process.StandardInput.BaseStream;
            try
            {
                await RenderAllFramesAsync(
                    timeline, fps, width, height, contentSource,
                    totalFrames, stdin, surfacePool);
            }
            finally
            {
                //always close stdin, or ffmpeg waits forever for more frames
                stdin.Close();
            }

            Exception? audioError = null;
            try { await audio; }
            catch (Exception ex) { audioError = ex; }

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"ffmpeg exited with code {process.ExitCode} rendering output:\n{stderr}");

            if (audioError is not null)
                throw new InvalidOperationException("Streaming the render's audio failed.", audioError);
        }

        //the whole timeline's audio, block by block, as f32le into ffmpeg's audio pipe; sources are waited for and failures go to the report
        private static async Task StreamAudioAsync(
            Timeline timeline, NamedPipeServerStream pipe, RenderReportBuilder report, CancellationToken ct)
        {
            await pipe.WaitForConnectionAsync(ct);

            await Task.Run(async () =>
            {
                var session = new AudioSession(new AudioFormat(AudioSampleRate, AudioChannelCount), waitForSources: true, report);
                using var master = new MasterAudioStream(timeline, session, 0, 1, PitchPreservation.Off);

                long total = session.FrameOf(timeline.Duration);
                var block = new float[session.BlockFrames * AudioChannelCount];

                for (long frame = 0; frame < total; frame += session.BlockFrames)
                {
                    int samples = (int)Math.Min(session.BlockFrames, total - frame) * AudioChannelCount;
                    master.Read(block.AsSpan(0, samples));
                    await pipe.WriteAsync(MemoryMarshal.AsBytes(block.AsSpan(0, samples)).ToArray(), ct);
                }

                await pipe.FlushAsync(ct);
            }, ct);

            pipe.Disconnect();
        }

        private static async Task RenderAllFramesAsync(
            Timeline timeline, int fps, int width, int height,
            ClipContentSource contentSource,
            int totalFrames, Stream accumulator, SurfacePool surfacePool)
        {
            var sw = Stopwatch.StartNew();

            long previousElapsedMs = 0;

            for (int frameIndex = 0; frameIndex < totalFrames; frameIndex++)
            {
                //an export always waits for every frame
                contentSource.Anticipate(timeline, frameIndex);
                FrameState state = FrameStateResolver.Resolve(timeline, frameIndex, fps);
                contentSource.WaitReady(state, System.Threading.Timeout.InfiniteTimeSpan);

                (byte[] buffer, int length) = FrameCompositor.RenderFrame(
                    state, contentSource, width, height, fps, surfacePool);

                try
                {
                    await accumulator.WriteAsync(buffer.AsMemory(0, length));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
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
                throw new ArgumentException("Blueprint.RenderSettings.Resolution must have positive width and height.");

            if (blueprint.RenderSettings.Framerate <= 0)
                throw new ArgumentException("Blueprint.RenderSettings.Framerate must be positive.");

            if (blueprint.RenderSettings.GpuAdapterIndex is < 0)
                throw new ArgumentException(
                    "Blueprint.RenderSettings.GpuAdapterIndex must be null (auto) or a non-negative " +
                    "DXGI adapter index. Valid indices for this machine are listed in the log at the " +
                    "start of every GPU session.");

            if (string.IsNullOrWhiteSpace(blueprint.OutputPath))
                throw new ArgumentException("Blueprint.OutputPath must be a full output file path.");

            //a fade to colour fills the whole canvas partway through, so above the
            //bottom video channel it would black out everything beneath it
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
