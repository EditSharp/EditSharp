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
    /// </summary>
    internal sealed class PlaybackStartGate
    {
        private readonly int _participantCount;
        private int _readyCount;
        private readonly TaskCompletionSource<bool> _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PlaybackStartGate(int participantCount)
        {
            if (participantCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(participantCount));

            _participantCount = participantCount;
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
                _gate.TrySetResult(true);

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
