using System;
using System.Threading;
using System.Threading.Tasks;
 
namespace EditSharp.Playback
{
    /// <summary>
    /// Lets Pause()/Resume() halt the video and audio loops IN PLACE —
    /// without cancelling them, so nothing they own (SourceDecoder
    /// instances, the GpuContext/SurfacePool, PlaybackAudioEngine's
    /// ffmpeg process) gets torn down the way Stop() tears it down. See
    /// Playback's own remarks on why Pause and Stop are deliberately
    /// different operations.
    ///
    /// Both loops check this at the top of every iteration — before
    /// rendering the next frame / reading the next audio chunk — so
    /// nothing new is PRODUCED while paused, but everything already open
    /// stays open. On the audio side specifically: not reading from the
    /// ffmpeg process's stdout pipe while paused means the OS pipe buffer
    /// fills and ffmpeg's own writes BLOCK — its decode pipeline
    /// self-throttles to a stop with zero signaling needed from us, the
    /// same backpressure SourceDecoder already relies on for ordinary
    /// pacing.
    ///
    /// Unlike PlaybackStartGate (fires exactly once, permanently open
    /// after), this one toggles — Pause()/Resume() can be called any
    /// number of times across a session.
    /// </summary>
    internal sealed class PlaybackPauseGate
    {
        private readonly object _lock = new();
        private TaskCompletionSource<bool>? _resumeSignal;
 
        public bool IsPaused
        {
            get { lock (_lock) return _resumeSignal != null; }
        }
 
        public void Pause()
        {
            lock (_lock)
            {
                _resumeSignal ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
 
        public void Resume()
        {
            TaskCompletionSource<bool>? signal;
            lock (_lock)
            {
                signal = _resumeSignal;
                _resumeSignal = null;
            }
 
            signal?.TrySetResult(true);
        }
 
        /// <summary>
        /// Returns immediately if not currently paused. If paused, waits
        /// until Resume() is called — or the token is cancelled (Stop()
        /// unblocks a paused loop this way, with no special-casing needed
        /// on Stop()'s part).
        /// </summary>
        public async Task WaitIfPausedAsync(CancellationToken token)
        {
            Task? waitTask;
            lock (_lock) { waitTask = _resumeSignal?.Task; }
            if (waitTask == null) return;
 
            await waitTask.WaitAsync(token);
        }
    }
}
 