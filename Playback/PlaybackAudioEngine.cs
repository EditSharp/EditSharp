using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using EditSharp.Audio.Engine;
using EditSharp.Components;
using EditSharp.Components.Clips;

namespace EditSharp.Playback
{
    /// <summary>
    /// Plays a session's audio: an AudioEngine renders the master up to
    /// EditSharpConfig.AudioLatency ahead, and this delivers it block by block
    /// (as s16le AudioSample events) on schedule, at any speed and in either
    /// direction.
    ///
    /// Delivery is "play this now": each block goes out when its own start is
    /// due. As the leader it keeps its own wall clock and reports positions to
    /// the reference clock; as a follower it waits for the reference clock to
    /// reach each block, and in FrameDropping it drops blocks that are already
    /// more than a block late instead of catching up.
    /// </summary>
    internal sealed class PlaybackAudioEngine : IDisposable
    {
        public const int SampleRate = 48000;
        public const int ChannelCount = 2;

        private AudioEngine? _engine;
        private Task? _pumpTask;

        public async Task StartAsync(
            Timeline timeline, TimeSpan startPosition, double speed, PitchPreservation pitch, AudioTaps taps,
            PlaybackStartGate startGate, PlaybackPauseGate pauseGate,
            PlaybackReferenceClock referenceClock, bool followsReferenceClock, bool dropsLateChunks,
            Action<AudioSampleEventArgs> onSample, CancellationToken token)
        {
            try
            {
                var session = new AudioSession(new AudioFormat(SampleRate, ChannelCount), waitForSources: false, taps: taps);
                var engine = new AudioEngine(timeline, session, startPosition, speed, pitch);
                _engine = engine;

                //waits for the sources audible at the start, so playback doesn't open on a gap
                await Task.Run(engine.Start, token);
            }
            catch (Exception ex)
            {
                startGate.Fault(ex);
                throw;
            }

            _pumpTask = Task.Run(
                () => PumpAsync(_engine, speed, startGate, pauseGate, referenceClock, followsReferenceClock, dropsLateChunks, onSample, token),
                token);
        }

        private static async Task PumpAsync(
            AudioEngine engine, double speed,
            PlaybackStartGate startGate, PlaybackPauseGate pauseGate,
            PlaybackReferenceClock referenceClock, bool followsReferenceClock, bool dropsLateChunks,
            Action<AudioSampleEventArgs> onSample, CancellationToken token)
        {
            try { await startGate.ReadyAndWaitAsync(token); }
            catch (OperationCanceledException) { return; }

            Stopwatch? clock = followsReferenceClock ? null : Stopwatch.StartNew();
            EditSharpConfig.Logger.LogVerbose(followsReferenceClock ? "Audio now following the reference clock." : "Audio pacing clock started.");

            int direction = Math.Sign(speed);
            double pace = Math.Abs(speed);
            long framesDelivered = 0;
            byte[] bytes = [];

            while (!token.IsCancellationRequested)
            {
                if (!await WaitWhilePausedAsync(pauseGate, clock, token)) return;

                AudioBlock? block;
                try { block = await engine.TakeAsync(token); }
                catch (OperationCanceledException) { return; }
                if (block is null) break;

                //due when everything before it has played: real time, whatever the speed
                TimeSpan target = TimeSpan.FromSeconds(framesDelivered / (double)SampleRate);
                TimeSpan position = block.Position;
                TimeSpan length = TimeSpan.FromSeconds(block.Frames / (double)SampleRate * pace);
                framesDelivered += block.Frames;

                //FrameDropping: already a whole block behind the clock
                if (followsReferenceClock && dropsLateChunks && (referenceClock.Position - position) * direction > length)
                    continue;

                while (true)
                {
                    if (token.IsCancellationRequested) return;
                    if (!await WaitWhilePausedAsync(pauseGate, clock, token)) return;

                    TimeSpan wait;
                    if (followsReferenceClock)
                    {
                        //timeline time until the clock reaches this block, in wall time
                        TimeSpan gap = (position - referenceClock.Position) * direction;
                        wait = gap > TimeSpan.Zero ? Max(gap / pace, PlaybackReferenceClock.PollInterval) : TimeSpan.Zero;
                    }
                    else
                    {
                        wait = target - clock!.Elapsed;
                    }

                    if (wait <= TimeSpan.Zero) break;

                    try { await Task.Delay(wait, token); }
                    catch (OperationCanceledException) { return; }
                }

                if (!followsReferenceClock) referenceClock.Report(position);

                int length16 = block.Samples.Length * 2;
                if (bytes.Length < length16) bytes = new byte[length16];
                ToInt16(block.Samples, bytes);

                onSample(new AudioSampleEventArgs(bytes, length16, SampleRate, ChannelCount, position));
            }
        }

        private static async Task<bool> WaitWhilePausedAsync(PlaybackPauseGate pauseGate, Stopwatch? clock, CancellationToken token)
        {
            if (!pauseGate.IsPaused) return true;

            clock?.Stop();
            try { await pauseGate.WaitIfPausedAsync(token); }
            catch (OperationCanceledException) { return false; }
            clock?.Start();
            return true;
        }

        private static void ToInt16(float[] samples, byte[] bytes)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                short value = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
                bytes[i * 2] = (byte)value;
                bytes[i * 2 + 1] = (byte)(value >> 8);
            }
        }

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

        public void Dispose()
        {
            _engine?.Dispose();
            _engine = null;
        }
    }
}
