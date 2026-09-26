using System;
using System.Diagnostics;

namespace EditSharp.Playback
{
    /// <summary>Where the leading stream is on the timeline, so the other stream can pace itself against it.</summary>
    /// <remarks>
    /// Which stream leads comes from <see cref="PlaybackMode"/>. The leader reports
    /// its position once per frame or audio block; between reports the position is
    /// extrapolated at <see cref="Rate"/> from the time of the last one, so a
    /// follower sees it move smoothly instead of in steps. The wall clock stops
    /// while paused, and doesn't start until <see cref="Begin"/>.
    /// </remarks>
    internal sealed class PlaybackReferenceClock
    {
        //the shortest wait a follower re-checking the leader uses; it waits for the estimated gap otherwise.
        //Task.Delay can't wait less than Windows' 15.6 ms timer tick anyway
        public static readonly Time PollInterval = Time.FromMilliseconds(2);

        private readonly object _lock = new();
        private readonly Stopwatch _wallClock = new();
        private Time _reportedPosition;
        private Time _reportedAt;

        //the clock holds its position until Begin, so nothing moves while playback is still waiting on its sources
        private bool _begun;
        private bool _paused;

        /// <summary>`rate` is timeline time per second of wall time: the playback speed, negative in reverse.</summary>
        public PlaybackReferenceClock(double rate = 1) => _rate = rate;

        /// <summary>Starts the clock: playback has really started.</summary>
        public void Begin()
        {
            lock (_lock)
            {
                _begun = true;
                if (!_paused) _wallClock.Start();
            }
        }

        private double _rate;
        public double Rate { get { lock (_lock) return _rate; } }

        public Time Position
        {
            get { lock (_lock) return PositionNow(); }
        }

        private Time PositionNow() => _reportedPosition + (Time.FromTimeSpan(_wallClock.Elapsed) - _reportedAt).Scale(_rate);

        /// <summary>Changes the rate from now on; the position carries on from where it is.</summary>
        public void SetRate(double rate)
        {
            lock (_lock)
            {
                if (rate == _rate) return;
                _reportedPosition = PositionNow();
                _reportedAt = Time.FromTimeSpan(_wallClock.Elapsed);
                _rate = rate;
            }
        }

        public void Report(Time position)
        {
            lock (_lock)
            {
                _reportedPosition = position;
                _reportedAt = Time.FromTimeSpan(_wallClock.Elapsed);
            }
        }

        public void PauseWallClock()
        {
            lock (_lock)
            {
                _paused = true;
                _wallClock.Stop();
            }
        }

        public void ResumeWallClock()
        {
            lock (_lock)
            {
                _paused = false;
                if (_begun) _wallClock.Start();
            }
        }
    }
}
