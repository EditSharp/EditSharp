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
    ///
    /// SYNCHRONIZED STARTUP / PAUSE: see PlaybackStartGate and
    /// PlaybackPauseGate's own remarks. This engine's ffmpeg process starts
    /// producing PCM into its stdout pipe as soon as it's spawned — before
    /// the delivery loop below has necessarily started pacing, and even
    /// while paused. That's fine by design in both cases: the OS pipe
    /// buffer absorbs a bounded amount of backlog, and once it fills,
    /// ffmpeg's own writes simply BLOCK — its decode pipeline self-throttles
    /// with no signaling needed from us.
    ///
    /// DELIVERY IS "PLAY THIS NOW" — NO LOOKAHEAD, DELIBERATELY. An earlier
    /// version of this delivered an unpaced AudioLeadTime burst before the
    /// synchronized start, plus a standing DeliveryCushion at steady state,
    /// so the consumer's output device would have backlog before pulling.
    /// Removed both — see Playback's own remarks for the full reasoning.
    /// Short version: the underrun problem that motivated them turned out
    /// to be an unrelated buffer-corruption bug (fixed separately, and
    /// confirmed by testing with zero pre-buffering afterward), and
    /// pre-buffering ahead of time is fundamentally incompatible with
    /// EveryFrame mode — a real output device drains on its own clock once
    /// started, so handing it backlog early doesn't let us control WHEN
    /// audio becomes audible, only how much backlog exists. Delivering
    /// exactly when due means delivery timing directly IS audible timing.
    ///
    /// LEADER / FOLLOWER (PlaybackReferenceClock): in SyncToAudio mode,
    /// this engine is the LEADER — own Stopwatch, delivers each chunk
    /// exactly when its own real-time schedule says it's due. In
    /// EveryFrame mode, it's the FOLLOWER instead — no local Stopwatch;
    /// paces against Playback's shared reference clock (written by video),
    /// same "deliver exactly when due" principle, just measured against
    /// the leader's reported position rather than our own Elapsed. See
    /// Playback's own remarks for the full mode mapping.
    ///
    /// WHAT THE LEADER REPORTS, PRECISELY: startPosition + clock.Elapsed —
    /// real elapsed time since our own pacing clock started. With no
    /// lookahead cushion anymore, this is now equal (not just close) to
    /// `position` (startPosition + targetElapsed, the content position of
    /// the chunk we're delivering) at the moment of delivery — the two
    /// diverging by a cushion amount was specifically a symptom of the
    /// pre-buffering design this reverts. Still computed via clock.Elapsed
    /// rather than targetElapsed here, on principle: it's the more direct
    /// measure of "what's actually true right now" and doesn't silently
    /// reintroduce a gap if a cushion ever comes back for some other
    /// reason later.
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
            TimeSpan startPosition,
            PlaybackStartGate startGate, PlaybackPauseGate pauseGate,
            PlaybackReferenceClock referenceClock, bool followsReferenceClock,
            Action<AudioSampleEventArgs> onSample, CancellationToken token)
        {
            try
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
            }
            catch (Exception ex)
            {
                //Video's ReadyAndWaitAsync would otherwise hang forever
                //waiting for an audio participant that's never coming — see
                //PlaybackStartGate's own remarks.
                startGate.Fault(ex);
                throw;
            }

            _pumpTask = Task.Run(
                () => PumpAsync(startPosition, startGate, pauseGate, referenceClock, followsReferenceClock, onSample, token),
                token);
        }

        private async Task PumpAsync(
            TimeSpan startPosition,
            PlaybackStartGate startGate, PlaybackPauseGate pauseGate,
            PlaybackReferenceClock referenceClock, bool followsReferenceClock,
            Action<AudioSampleEventArgs> onSample, CancellationToken token)
        {
            if (_process == null) return;

            Stream stdout = _process.StandardOutput.BaseStream;
            byte[] buffer = new byte[ChunkBytes];

            // Discard audio before startPosition as fast as the pipe will
            // give it up — no pacing here, this is "seek by skipping,"
            // flagged above as the known-inefficient path. Deliberately NOT
            // gated on startGate — this is real seek-related work, not
            // setup, and shouldn't be held back waiting for video.
            long bytesToSkip = (long)(startPosition.TotalSeconds * BytesPerSecond);
            bytesToSkip -= bytesToSkip % BytesPerFrame;

            while (bytesToSkip > 0 && !token.IsCancellationRequested)
            {
                int toRead = (int)Math.Min(buffer.Length, bytesToSkip);
                int read = await stdout.ReadAsync(buffer.AsMemory(0, toRead), token);
                if (read <= 0) return; // timeline shorter than requested start
                bytesToSkip -= read;
            }

            long bytesDelivered = 0;

            //Setup (and skip, if any) is done — wait for video to also be
            //ready before starting the clock. Nothing between this line and
            //Stopwatch.StartNew() should do real work.
            try { await startGate.ReadyAndWaitAsync(token); }
            catch (OperationCanceledException) { return; }

            //LEADER-ONLY: own Stopwatch. A FOLLOWER paces against
            //referenceClock.Position (written by video) instead.
            Stopwatch? clock = followsReferenceClock ? null : Stopwatch.StartNew();
            EditSharpConfig.Logger.LogVerbose(followsReferenceClock
                ? "Audio now following the reference clock."
                : "Audio pacing clock started.");

            while (!token.IsCancellationRequested)
            {
                //Pause check BEFORE reading the next chunk — nothing new
                //gets pulled off the pipe while paused (see class remarks
                //on why that's enough to self-throttle ffmpeg too).
                if (pauseGate.IsPaused)
                {
                    clock?.Stop();
                    try { await pauseGate.WaitIfPausedAsync(token); }
                    catch (OperationCanceledException) { break; }
                    clock?.Start();
                }

                int read = await stdout.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                if (read <= 0) break; // process EOF — timeline audio exhausted

                bytesDelivered += read;
                TimeSpan targetElapsed = TimeSpan.FromSeconds(bytesDelivered / (double)BytesPerSecond);
                TimeSpan position = startPosition + targetElapsed;

                //Second pause check — the wait below (leader or follower)
                //can take a while, and Pause() might land during it.
                //Re-checks both conditions after each wait since either
                //could change while waiting on the other.
                while (true)
                {
                    if (token.IsCancellationRequested) return;

                    if (pauseGate.IsPaused)
                    {
                        clock?.Stop();
                        try { await pauseGate.WaitIfPausedAsync(token); }
                        catch (OperationCanceledException) { return; }
                        clock?.Start();
                        continue;
                    }

                    if (followsReferenceClock)
                    {
                        //FOLLOWER: deliver exactly when the leader reaches
                        //this chunk's due time — no lookahead. Computes the
                        //actual expected wait rather than polling in fixed
                        //increments — see PlaybackReferenceClock's remarks.
                        TimeSpan gap = position - referenceClock.Position;

                        if (gap > TimeSpan.Zero)
                        {
                            TimeSpan wait = gap > PlaybackReferenceClock.PollInterval
                                ? gap : PlaybackReferenceClock.PollInterval;

                            try { await Task.Delay(wait, token); }
                            catch (OperationCanceledException) { return; }
                            continue;
                        }
                    }
                    else
                    {
                        //LEADER: deliver exactly when due against our own
                        //Stopwatch — no standing cushion. Self-correcting
                        //by construction: if one delay overshoots, the next
                        //chunk's targetElapsed-vs-actualElapsed comparison
                        //automatically computes a shorter (or zero) wait to
                        //compensate, so error doesn't accumulate.
                        TimeSpan actualElapsed = clock!.Elapsed;

                        if (targetElapsed > actualElapsed)
                        {
                            try { await Task.Delay(targetElapsed - actualElapsed, token); }
                            catch (OperationCanceledException) { return; }
                            continue;
                        }
                    }

                    break;
                }

                if (!followsReferenceClock) referenceClock.Report(startPosition + clock!.Elapsed);

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
