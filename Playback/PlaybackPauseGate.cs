using System;
using System.Threading;
using System.Threading.Tasks;

namespace EditSharp.Playback
{
    /// <summary>Holds the video and audio loops in place while paused, without tearing down what they have open.</summary>
    /// <remarks>Both loops check it before producing each frame or block, so nothing new is produced while paused. It toggles as often as Pause and Resume are called.</remarks>
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

        //returns at once unless paused; otherwise waits for Resume, or for `token` (which is how Stop unblocks it)
        public async Task WaitIfPausedAsync(CancellationToken token)
        {
            Task? waitTask;
            lock (_lock) { waitTask = _resumeSignal?.Task; }
            if (waitTask == null) return;

            await waitTask.WaitAsync(token);
        }
    }
}
