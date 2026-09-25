using System;
using System.Threading;
using System.Threading.Tasks;
 
namespace EditSharp.Playback
{
    /// <summary>
    /// Coordinates the video loop and (if present) the audio engine so both
    /// start their PACING CLOCKS at the same real moment, instead of
    /// whenever each one's own setup happens to finish.
    ///
    /// THIS IS THE FIX for the video/audio desync bug from the first full
    /// playback test. The actual mechanism: video's setup
    /// (ContentPreparation — MediaProbe + FfmpegRunner
    /// .GetDecodePlanAsync per video clip, plus GpuContext/SurfacePool
    /// construction) is genuinely heavier than audio's setup (build one
    /// filter graph, spawn one ffmpeg process). Without this gate, that
    /// asymmetry means the two loops' own Stopwatch.StartNew() calls fire
    /// at different real wall-clock moments — a ONE-TIME OFFSET baked in at
    /// startup. (Correction from an earlier pass at this bug: this isn't
    /// ongoing "clock drift" — two Stopwatch instances read the same
    /// underlying hardware counter and don't meaningfully diverge from each
    /// other over a session. It's specifically this startup asymmetry.)
    ///
    /// NOTE ON A REMOVED FEATURE: an earlier version of this gate had a
    /// symmetric "pre-roll" hold — everyone waits an extra fixed duration
    /// after being ready, together. Removed: it turned out not to serve a
    /// real purpose once the actual audio corruption bug (a different bug
    /// entirely, in PlaybackAudioEngine's buffer reuse) was fixed — a
    /// uniform hold on BOTH streams doesn't help a consumer that needs
    /// audio-specific backlog before its output device starts. See
    /// Playback.AudioLeadTime / PlaybackAudioEngine's burst-delivery
    /// remarks for the mechanism that actually addresses that.
    ///
    /// "Ready," for an audio participant, now means "setup done AND (if
    /// configured) its lead burst has been delivered" — the gate itself
    /// doesn't know or care about that distinction; it just waits for
    /// however many participants were declared.
    /// </summary>
    internal sealed class PlaybackStartGate
    {
        private readonly int _participantCount;
        private readonly Action? _onReleased;
        private int _readyCount;
        private readonly TaskCompletionSource<bool> _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
 
        /// <param name="onReleased">
        /// Invoked exactly once, synchronously, the instant every
        /// participant has arrived — right before the gate actually opens.
        /// Runs on whichever participant's thread happens to be the last
        /// to arrive (not a fixed thread) — Playback uses this to raise
        /// PlaybackStarted.
        /// </param>
        public PlaybackStartGate(int participantCount, Action? onReleased = null)
        {
            if (participantCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(participantCount));
 
            _participantCount = participantCount;
            _onReleased = onReleased;
        }
 
        /// <summary>
        /// Called once a participant (the video loop, the audio pump loop)
        /// has finished its own setup and is ready to start its pacing
        /// clock. Returns once EVERY participant has reached this point —
        /// the caller's very next line should be Stopwatch.StartNew(),
        /// with nothing else in between that could reintroduce a real-time
        /// gap.
        /// </summary>
        public async Task ReadyAndWaitAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref _readyCount) >= _participantCount)
            {
                _onReleased?.Invoke();
                _gate.TrySetResult(true);
            }
 
            await _gate.Task.WaitAsync(token);
        }
 
        /// <summary>
        /// Called by a participant whose OWN setup failed, so the other
        /// participant doesn't hang at ReadyAndWaitAsync waiting forever
        /// for a partner that's never coming — e.g. audio's ffmpeg process
        /// failing to spawn would otherwise silently deadlock the video
        /// loop. Flagged and closed here rather than left as a latent hang.
        /// </summary>
        public void Fault(Exception exception)
        {
            _gate.TrySetException(exception);
        }
    }
}
 