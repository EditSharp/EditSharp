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
    /// AUDIO LEAD BURST (Playback.AudioLeadTime): before the synchronized
    /// start, this engine can deliver up to AudioLeadTime worth of chunks
    /// completely UNPACED — no Task.Delay, as fast as the pipe gives it up
    /// — so the consumer's own buffer (e.g. NAudio's BufferedWaveProvider)
    /// has real backlog before its output device starts pulling. This does
    /// NOT create a content-position offset from video: the burst chunks
    /// sit unplayed in the consumer's buffer until the consumer starts its
    /// device (on PlaybackStarted), and bytesDelivered simply carries
    /// through from the burst into the paced loop below with the SAME
    /// Stopwatch starting fresh right after the burst — so the very first
    /// post-burst pacing check naturally computes a large "ahead of
    /// schedule" delay equal to AudioLeadTime, which is exactly the real
    /// time the consumer's device needs to drain that backlog before
    /// wanting more. No separate offset math needed anywhere for this to
    /// come out correct. Happens regardless of leader/follower role, below
    /// — priming the output device is orthogonal to which stream paces
    /// which.
    ///
    /// LEADER / FOLLOWER (PlaybackReferenceClock): in SyncToAudio mode,
    /// this engine is the LEADER — behavior below is completely unchanged
    /// from before PlaybackMode was wired: own Stopwatch, own
    /// DeliveryCushion-based pacing. In EveryFrame mode, it's the FOLLOWER
    /// instead — no local Stopwatch; paces against Playback's shared
    /// reference clock (written by video) with the SAME DeliveryCushion
    /// concept, just measured against the leader's reported position
    /// rather than our own Elapsed. See Playback's own remarks for the
    /// full mode mapping.
    ///
    /// WHAT THE LEADER REPORTS, PRECISELY: startPosition + clock.Elapsed —
    /// real elapsed time since our own pacing clock started — NOT
    /// startPosition + targetElapsed (the content position of the chunk
    /// we're about to hand off). Those two are consistently offset by
    /// ~DeliveryCushion, by design: we deliver chunks ahead of when
    /// they're actually needed, on purpose, to keep the consumer's output
    /// device from underrunning. Reporting the handed-off position instead
    /// of the real-elapsed one would make a follower (video, in
    /// EveryFrame) pace itself ~DeliveryCushion ahead of what's actually
    /// audible — a real bug an earlier version of this had, not a
    /// hypothetical one.
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

        // How much content should always remain buffered-but-unconsumed in
        // the CONSUMER's buffer at minimum — a standing cushion, not a
        // "shave a few ms off" margin. This has to be meaningfully larger
        // than ChunkBytes' own duration (~100ms) to do anything at all — an
        // earlier version subtracted a small margin from a target that
        // already included one chunk's duration, which nearly canceled out
        // and delivered right at the underrun boundary instead of
        // meaningfully before it. Applies identically whether we're
        // measuring against our own Stopwatch (leader) or the reference
        // clock (follower) — same concept either way. Deliberately a fixed
        // internal constant, not a public knob.
        private static readonly TimeSpan DeliveryCushion = TimeSpan.FromMilliseconds(300);

        private Process? _process;
        private Task? _pumpTask;
        private readonly ConcurrentBag<string> _tempFiles = new();

        public async Task StartAsync(
            Timeline timeline, int fps, int canvasWidth, int canvasHeight,
            TimeSpan startPosition, TimeSpan audioLeadTime,
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
                () => PumpAsync(startPosition, audioLeadTime, startGate, pauseGate, referenceClock, followsReferenceClock, onSample, token),
                token);
        }

        private async Task PumpAsync(
            TimeSpan startPosition, TimeSpan audioLeadTime,
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

            // AUDIO LEAD BURST — see class remarks. Unpaced on purpose: no
            // Task.Delay here at all, just read-and-deliver as fast as the
            // pipe allows, until AudioLeadTime worth has gone out. Happens
            // regardless of leader/follower role.
            long leadBytes = (long)(audioLeadTime.TotalSeconds * BytesPerSecond);
            leadBytes -= leadBytes % BytesPerFrame;

            while (bytesDelivered < leadBytes && !token.IsCancellationRequested)
            {
                int toRead = (int)Math.Min(buffer.Length, leadBytes - bytesDelivered);
                int read = await stdout.ReadAsync(buffer.AsMemory(0, toRead), token);
                if (read <= 0) break; // timeline shorter than the requested lead

                bytesDelivered += read;
                TimeSpan burstPosition = startPosition + TimeSpan.FromSeconds(bytesDelivered / (double)BytesPerSecond);
                onSample(new AudioSampleEventArgs(buffer, read, SampleRate, ChannelCount, burstPosition));
            }

            //Lead burst (if any) is delivered — now wait for video to also
            //be ready, exactly like before, and start pacing for real.
            //bytesDelivered already reflects the burst, so (when leading)
            //the very first post-burst pacing check below computes the
            //correct catch-up delay automatically — see class remarks.
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
                        //FOLLOWER: same standing-cushion concept as the
                        //leader case, just measured against the leader's
                        //reported position instead of our own Elapsed.
                        if (referenceClock.Position < position - DeliveryCushion)
                        {
                            try { await Task.Delay(PlaybackReferenceClock.PollInterval, token); }
                            catch (OperationCanceledException) { return; }
                            continue;
                        }
                    }
                    else
                    {
                        //LEADER: maintain a STANDING CUSHION of buffered-
                        //but-unconsumed content — see class remarks for why
                        //this isn't "deliver exactly on schedule."
                        TimeSpan actualElapsed = clock!.Elapsed;
                        TimeSpan bufferedAhead = targetElapsed - actualElapsed;

                        if (bufferedAhead > DeliveryCushion)
                        {
                            try { await Task.Delay(bufferedAhead - DeliveryCushion, token); }
                            catch (OperationCanceledException) { return; }
                            continue;
                        }
                    }

                    break;
                }

                if (!followsReferenceClock)
                {
                    //Report REAL elapsed time since the leader's own clock
                    //started (startPosition + clock.Elapsed), NOT `position`
                    //(startPosition + targetElapsed, i.e. content already
                    //HANDED to the consumer's buffer). Those two are
                    //consistently offset by ~DeliveryCushion, by design —
                    //we deliver chunks ahead of when they're needed, on
                    //purpose, to prevent underrun. A follower reading the
                    //handed-off position instead of the real-time position
                    //ends up pacing itself ~DeliveryCushion ahead of what's
                    //actually audible — exactly the "audio drags behind
                    //video" symptom this replaces.
                    referenceClock.Report(startPosition + clock!.Elapsed);
                }

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
