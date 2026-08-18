using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EditSharp;
using EditSharp.Components;
using EditSharp.Components.Clips;
using EditSharp.Render;

namespace EditSharp.Playback
{
    /// <summary>
    /// Streams the timeline's mixed audio as raw PCM via a single long-lived
    /// ffmpeg process, reusing the exact same InputGraph / ClipContentBuilder
    /// / AudioMixer pipeline Renderer.FinalizeOutputAsync uses for the final
    /// mux — same filter graph construction, just piped to stdout as raw
    /// samples instead of written to a container file.
    ///
    /// KNOWN GAPS, FLAGGED RATHER THAN HIDDEN (v1 scope):
    ///   - AudioMixer.Compose always builds the mix for the WHOLE timeline
    ///     starting at t=0 (that's what a one-shot final render needs). This
    ///     engine reuses it as-is and just discards decoded audio before
    ///     Position rather than trimming the graph itself, so starting
    ///     playback deep into a long timeline pays real ffmpeg decode cost
    ///     for everything before Position, not just wall-clock delay. A
    ///     proper fix means either per-clip atrim offsets in the graph or an
    ///     output-level -ss, neither implemented here.
    ///   - Only real-time (1x) playback is supported. Playback.Play() gates
    ///     this engine to Speed == 1 and skips audio entirely otherwise —
    ///     see Playback's own remarks for why resampling PCM to arbitrary
    ///     speeds without also touching pitch is a separate piece of work,
    ///     not attempted here.
    /// </summary>
    internal sealed class PlaybackAudioEngine : IDisposable
    {
        public const int SampleRate = 48000;
        public const int ChannelCount = 2;
        private const int BytesPerSample = 2; // s16le
        private const int BytesPerFrame = ChannelCount * BytesPerSample;
        private const int BytesPerSecond = SampleRate * BytesPerFrame;

        // ~100ms per chunk — small enough for reasonably responsive pacing,
        // large enough not to make a syscall per handful of samples.
        private const int ChunkBytes = BytesPerSecond / 10 - (BytesPerSecond / 10 % BytesPerFrame);

        private Process? _process;
        private Task? _pumpTask;
        private readonly ConcurrentBag<string> _tempFiles = new();

        public async Task StartAsync(
            Timeline timeline, int fps, int canvasWidth, int canvasHeight,
            TimeSpan startPosition, Action<AudioSampleEventArgs> onSample,
            CancellationToken token)
        {
            var graph = new InputGraph();
            var contents = new Dictionary<Clip, ClipContent>();

            foreach (Channel channel in timeline.Channels)
            {
                foreach (Clip clip in channel.Clips.Values)
                {
                    if (contents.ContainsKey(clip)) continue;

                    contents[clip] = await ClipContentBuilder.BuildAsync(
                        clip, graph, canvasWidth, canvasHeight, fps, _tempFiles, audioOnly: true);
                }
            }

            string audioLabel = AudioMixer.Compose(timeline, contents, graph);

            string filterComplex = string.Join(";", graph.FilterLines);
            string scriptPath = GraphUtilities.GetVideoTempFilePath($"playback_audiofilter_{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(scriptPath, filterComplex, token);
            _tempFiles.Add(scriptPath);

            var args = new List<string> { "-y", "-v", "error" };
            args.AddRange(GraphUtilities.FilterThreadingArgs());

            foreach (var input in graph.Inputs)
            {
                if (input.ExtraArgs != null) args.AddRange(input.ExtraArgs);
                args.Add("-i");
                args.Add(input.Path);
            }

            args.Add("-/filter_complex");
            args.Add(scriptPath);
            args.Add("-map");
            args.Add($"[{audioLabel}]");
            args.Add("-f");
            args.Add("s16le");
            args.Add("-ar");
            args.Add(SampleRate.ToString());
            args.Add("-ac");
            args.Add(ChannelCount.ToString());
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

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Start();

            _pumpTask = Task.Run(() => PumpAsync(startPosition, onSample, token), token);
        }

        private async Task PumpAsync(TimeSpan startPosition, Action<AudioSampleEventArgs> onSample, CancellationToken token)
        {
            if (_process == null) return;

            Stream stdout = _process.StandardOutput.BaseStream;
            byte[] buffer = new byte[ChunkBytes];

            // Discard audio before startPosition as fast as the pipe will
            // give it up — no pacing here, this is "seek by skipping,"
            // flagged above as the known-inefficient path.
            long bytesToSkip = (long)(startPosition.TotalSeconds * BytesPerSecond);
            bytesToSkip -= bytesToSkip % BytesPerFrame;

            while (bytesToSkip > 0 && !token.IsCancellationRequested)
            {
                int toRead = (int)Math.Min(buffer.Length, bytesToSkip);
                int read = await stdout.ReadAsync(buffer.AsMemory(0, toRead), token);
                if (read <= 0) return; // timeline shorter than requested start
                bytesToSkip -= read;
            }

            var clock = Stopwatch.StartNew();
            long bytesDelivered = 0;

            while (!token.IsCancellationRequested)
            {
                int read = await stdout.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                if (read <= 0) break; // process EOF — timeline audio exhausted

                bytesDelivered += read;

                TimeSpan targetElapsed = TimeSpan.FromSeconds(bytesDelivered / (double)BytesPerSecond);
                TimeSpan actualElapsed = clock.Elapsed;
                if (targetElapsed > actualElapsed)
                {
                    try { await Task.Delay(targetElapsed - actualElapsed, token); }
                    catch (OperationCanceledException) { break; }
                }

                TimeSpan position = startPosition + targetElapsed;
                onSample(new AudioSampleEventArgs(buffer, read, SampleRate, ChannelCount, position));
            }
        }

        public void Dispose()
        {
            try
            {
                if (_process is { HasExited: false })
                    _process.Kill(entireProcessTree: true);
            }
            catch { /* best-effort */ }

            _process?.Dispose();

            foreach (string path in _tempFiles)
            {
                try { File.Delete(path); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
