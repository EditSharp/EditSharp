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
    /// (RenderContentPreparation — MediaProbe + FfmpegRunner
    /// .GetDecodePlanAsync per video clip, plus GpuContext/SkSurfacePool
    /// construction) is genuinely heavier than audio's setup (build one
    /// filter graph, spawn one ffmpeg process). Without this gate, that
    /// asymmetry means the two loops' own Stopwatch.StartNew() calls fire
    /// at different real wall-clock moments — a ONE-TIME OFFSET baked in at
    /// startup. (Correction from an earlier pass at this bug: this isn't
    /// ongoing "clock drift" — two Stopwatch instances read the same
    /// underlying hardware counter and don't meaningfully diverge from each
    /// other over a session. It's specifically this startup asymmetry.)
    ///
    /// PRE-ROLL: an optional additional hold, applied AFTER every
    /// participant is ready, before releasing anyone. Exists for a second,
    /// related problem the gate alone doesn't solve — a real playback
    /// consumer (an actual audio output device) typically needs its own
    /// backlog buffered before it can start outputting without underrun,
    /// and has no equivalent concept for video. Without a shared hold,
    /// video would start being visibly displayed the instant the gate
    /// opens while a well-behaved consumer is still deliberately holding
    /// audio output back to build backlog — reintroducing a startup
    /// video-ahead-of-audio gap, just for a legitimate reason this time
    /// instead of a bug. Pre-roll gives a consumer a defined window to do
    /// that buffering in, with BOTH streams held back identically during
    /// it. IMPORTANT: this alone does not fix an audio consumer that starts
    /// its output device immediately regardless of backlog — the consumer
    /// still has to actually use the window (defer starting output until
    /// real backlog exists, e.g. until it's received its first few
    /// AudioSample chunks) for pre-roll to help. Defaults to zero — no
    /// change to prior behavior unless explicitly set.
    /// </summary>
    internal sealed class PlaybackStartGate
    {
        private readonly int _participantCount;
        private readonly TimeSpan _preRoll;
        private int _readyCount;
        private readonly TaskCompletionSource<bool> _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PlaybackStartGate(int participantCount, TimeSpan preRoll = default)
        {
            if (participantCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(participantCount));

            if (preRoll < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(preRoll));

            _participantCount = participantCount;
            _preRoll = preRoll;
        }

        /// <summary>
        /// Called once a participant (the video loop, the audio pump loop)
        /// has finished its own setup and is ready to start its pacing
        /// clock. Returns once EVERY participant has reached this point AND
        /// (if configured) the pre-roll hold has elapsed — the caller's
        /// very next line should be Stopwatch.StartNew(), with nothing else
        /// in between that could reintroduce a real-time gap.
        /// </summary>
        public async Task ReadyAndWaitAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref _readyCount) >= _participantCount)
            {
                if (_preRoll > TimeSpan.Zero)
                {
                    //Fire-and-forget is deliberate: whichever participant
                    //happens to be last to arrive here shouldn't itself be
                    //the one sitting in Task.Delay — every participant
                    //(including this one) resumes together from the SAME
                    //_gate.Task completing below, not from this call
                    //returning.
                    _ = ReleaseAfterPreRollAsync(token);
                }
                else
                {
                    _gate.TrySetResult(true);
                }
            }

            await _gate.Task.WaitAsync(token);
        }

        private async Task ReleaseAfterPreRollAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(_preRoll, token);
                _gate.TrySetResult(true);
            }
            catch (OperationCanceledException)
            {
                _gate.TrySetCanceled(token);
            }
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
