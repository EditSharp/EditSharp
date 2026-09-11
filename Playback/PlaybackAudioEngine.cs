using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using EditSharp;
using EditSharp.Audio;
using EditSharp.Components;
 
namespace EditSharp.Playback
{
    /// <summary>
    /// Delivers the timeline's mixed audio as raw PCM chunks, sliced
    /// directly out of an in-memory master buffer.
    ///
    /// REAL-AUDIO-PIPELINE REWRITE: this used to spawn a long-lived ffmpeg
    /// process (reusing InputGraph/ClipContentBuilder/AudioMixer's
    /// filter-line-building) and pipe its stdout as raw PCM. That entire
    /// subprocess is gone. StartAsync now just asks AudioMixer.ComposeAsync
    /// for the timeline's fully mixed, fully graph-evaluated master
    /// AudioBuffer ONCE (same call FinalizeOutputAsync makes for the final
    /// render — same real per-node audio graph evaluation, so a live preview
    /// and a final render of the same timeline are mixed identically), converts
    /// it once to s16le bytes (AudioSampleEventArgs' own wire format), and the
    /// pump loop slices directly out of that byte array instead of reading
    /// from a pipe. No ffmpeg process, no filter script temp file, no pipe
    /// backpressure to reason about for this engine at all any more.
    ///
    /// KNOWN LIMITATION, still flagged (unchanged from before): only
    /// real-time (1x) playback is supported — Playback.Play() gates this
    /// engine to Speed == 1 and skips audio entirely otherwise, same as
    /// always.
    ///
    /// SYNCHRONIZED STARTUP / PAUSE: see PlaybackStartGate and
    /// PlaybackPauseGate's own remarks.
    ///
    /// DELIVERY IS "PLAY THIS NOW" — NO LOOKAHEAD, DELIBERATELY. This means
    /// the target time for a chunk must be the chunk's OWN START position
    /// (how much was already delivered BEFORE it), not its end — see
    /// PumpAsync's own remarks on a real bug this used to have.
    ///
    /// LEADER / FOLLOWER (PlaybackReferenceClock): in SyncToAudio mode,
    /// this engine is the LEADER — own Stopwatch, delivers each chunk
    /// exactly when its own real-time schedule says it's due. In
    /// EveryFrame mode, it's the FOLLOWER instead.
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
 
        private byte[] _pcm = [];
        private Task? _pumpTask;
 
        public async Task StartAsync(
            Timeline timeline, int fps, int canvasWidth, int canvasHeight,
            TimeSpan startPosition,
            PlaybackStartGate startGate, PlaybackPauseGate pauseGate,
            PlaybackReferenceClock referenceClock, bool followsReferenceClock,
            Action<AudioSampleEventArgs> onSample, CancellationToken token)
        {
            try
            {
                AudioBuffer master = await AudioMixer.ComposeAsync(timeline, SampleRate, ChannelCount, token);
                _pcm = master.ToInt16Bytes();
            }
            catch (Exception ex)
            {
                startGate.Fault(ex);
                throw;
            }
 
            _pumpTask = Task.Run(
                () => PumpAsync(startPosition, startGate, pauseGate, referenceClock, followsReferenceClock, onSample, token),
                token);
        }
 
        /// <summary>
        /// FOUND IN THE FIELD, FIXED: `bytesDelivered` used to be
        /// incremented BEFORE computing `targetElapsed`/`position` for the
        /// chunk about to be delivered, so both were computed against the
        /// byte count AS OF THE END of that chunk rather than its start.
        /// Since this runs for every chunk starting with the very first
        /// one, it meant chunk 0 (which should deliver immediately at
        /// t=0, per this class's own "no lookahead" contract) instead
        /// waited until the pacing clock reached one whole ChunkBytes'
        /// worth of elapsed time (~100ms) — and every later chunk was
        /// delivered exactly one chunk-length later than it should have
        /// been, a constant ~100ms of audible startup silence plus a
        /// persistent ~100ms A/V sync offset for the rest of the session.
        /// Fix: compute the target position from `bytesDelivered` as it
        /// stood BEFORE this chunk (the chunk's own start), THEN advance
        /// it by `toDeliver` for the next iteration.
        /// </summary>
        private async Task PumpAsync(
            TimeSpan startPosition,
            PlaybackStartGate startGate, PlaybackPauseGate pauseGate,
            PlaybackReferenceClock referenceClock, bool followsReferenceClock,
            Action<AudioSampleEventArgs> onSample, CancellationToken token)
        {
            long byteOffset = (long)(startPosition.TotalSeconds * BytesPerSecond);
            byteOffset -= byteOffset % BytesPerFrame;
 
            if (byteOffset >= _pcm.Length) return; // timeline shorter than requested start
 
            long bytesDelivered = 0;
 
            try { await startGate.ReadyAndWaitAsync(token); }
            catch (OperationCanceledException) { return; }
 
            Stopwatch? clock = followsReferenceClock ? null : Stopwatch.StartNew();
            EditSharpConfig.Logger.LogVerbose(followsReferenceClock
                ? "Audio now following the reference clock."
                : "Audio pacing clock started.");
 
            byte[] chunk = new byte[ChunkBytes];
 
            while (!token.IsCancellationRequested)
            {
                if (pauseGate.IsPaused)
                {
                    clock?.Stop();
                    try { await pauseGate.WaitIfPausedAsync(token); }
                    catch (OperationCanceledException) { break; }
                    clock?.Start();
                }
 
                long remaining = _pcm.Length - byteOffset;
                if (remaining <= 0) break; // timeline audio exhausted
 
                int toDeliver = (int)Math.Min(chunk.Length, remaining);
                Array.Copy(_pcm, byteOffset, chunk, 0, toDeliver);
                byteOffset += toDeliver;
 
                // Target/position computed from bytes delivered BEFORE this
                // chunk (its start), not after (its end) — see the method's
                // own remarks.
                TimeSpan targetElapsed = TimeSpan.FromSeconds(bytesDelivered / (double)BytesPerSecond);
                TimeSpan position = startPosition + targetElapsed;
                bytesDelivered += toDeliver;
 
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
 
                onSample(new AudioSampleEventArgs(chunk, toDeliver, SampleRate, ChannelCount, position));
            }
        }
 
        public void Dispose()
        {
            //nothing to tear down any more — no process, no temp files: the
            //master PCM is a plain managed byte array, reclaimed by the GC
            //like everything else once this engine drops its reference.
        }
    }
}
 