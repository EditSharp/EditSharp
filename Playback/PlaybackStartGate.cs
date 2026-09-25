using System;
using System.Threading;
using System.Threading.Tasks;

namespace EditSharp.Playback
{
    /// <summary>Starts the video loop and audio engine at the same moment, whenever each finishes its own setup.</summary>
    /// <remarks>Video's setup takes longer than audio's; without the gate the two would start their clocks apart and stay offset for the whole session.</remarks>
    internal sealed class PlaybackStartGate
    {
        private readonly int _participantCount;
        private readonly Action? _onReleased;
        private int _readyCount;
        private readonly TaskCompletionSource<bool> _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <param name="participantCount">How many participants must arrive.</param>
        /// <param name="onReleased">Runs once, on the last participant's thread, just before the gate opens.</param>
        public PlaybackStartGate(int participantCount, Action? onReleased = null)
        {
            if (participantCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(participantCount));

            _participantCount = participantCount;
            _onReleased = onReleased;
        }

        //a participant's setup is done; returns when every participant's is. Start timing straight after
        public async Task ReadyAndWaitAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref _readyCount) >= _participantCount)
            {
                _onReleased?.Invoke();
                _gate.TrySetResult(true);
            }

            await _gate.Task.WaitAsync(token);
        }

        //a participant's setup failed: release the others instead of leaving them waiting
        public void Fault(Exception exception)
        {
            _gate.TrySetException(exception);
        }
    }
}
