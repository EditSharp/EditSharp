using System;
using System.Diagnostics;

namespace EditSharp.Playback
{
    /// <summary>
    /// Publishes "where the LEADER stream currently is" (absolute timeline
    /// position) so a FOLLOWER stream can pace itself against the leader's
    /// actual position instead of its own independent Stopwatch. Which
    /// stream leads vs follows is decided once per session, from
    /// PlaybackMode — see Playback's own remarks for the full mapping.
    ///
    /// EXTRAPOLATED, NOT JUST A SNAPSHOT — this is the actual fix for
    /// persistent follower jitter, and it's worth understanding exactly
    /// why. Report() is called once per LEADER unit of work — once per
    /// ~100ms audio chunk, or once per video frame (~33ms at 30fps). A
    /// naive "just store the last reported value" design means Position
    /// is FROZEN between those calls — a follower checking at, say, 2ms
    /// granularity sees the SAME stale value repeatedly for up to a whole
    /// leader-interval, then a sudden jump. At 30fps against a 100ms-
    /// granularity audio leader, that's roughly 3 video frames per jump —
    /// which shows up as exactly the clumped, stuttery delivery pattern
    /// reported ("jitter... unlike the pre-PlaybackModes perfect
    /// smoothness"), no matter how precisely a follower computes its own
    /// wait against that stale value. Fixing follower-side wait precision
    /// (an earlier pass here did that) couldn't touch this — the SOURCE
    /// value itself wasn't fresh enough to wait precisely against.
    ///
    /// The fix: track WHEN each report happened (against an internal
    /// Stopwatch), and have Position EXTRAPOLATE forward from the last
    /// report by however much real time has passed since — smoothly
    /// continuous, not staircase. This assumes the leader progresses at
    /// real 1x speed between its own reports, which is true for both
    /// audio's real-time PCM pacing and video's Speed-scaled Stopwatch
    /// pacing.
    ///
    /// PAUSE-AWARE: the internal Stopwatch must be explicitly stopped/
    /// started around a pause (PauseWallClock/ResumeWallClock) — Playback
    /// calls these from Pause() and Play()'s unpause path. Without this,
    /// extrapolation would keep advancing Position through an entire pause
    /// window (the Stopwatch doesn't know about PlaybackPauseGate on its
    /// own), producing a wrong forward jump the instant playback resumes.
    /// </summary>
    internal sealed class PlaybackReferenceClock
    {
        // Minimum/fallback wait for a follower re-checking the leader's
        // position — NOT the primary wait mechanism. A follower computes
        // an actual expected wait from the position gap and sleeps for
        // approximately that (self-correcting, same spirit as a leader's
        // own Stopwatch-based delay); this is only used when that estimate
        // is smaller than this floor, or to re-check promptly after an
        // estimate that turned out to be wrong. Task.Delay below Windows'
        // default ~15.6ms system timer resolution floor doesn't actually
        // achieve the requested duration, which is why this is small but
        // not relied on as the primary precision mechanism.
        public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(2);

        private readonly object _lock = new();
        private readonly Stopwatch _wallClock = new();
        private TimeSpan _reportedPosition;
        private TimeSpan _reportedAt;

        public PlaybackReferenceClock()
        {
            _wallClock.Start();
        }

        public TimeSpan Position
        {
            get
            {
                TimeSpan reportedPosition, reportedAt;
                lock (_lock)
                {
                    reportedPosition = _reportedPosition;
                    reportedAt = _reportedAt;
                }

                return reportedPosition + (_wallClock.Elapsed - reportedAt);
            }
        }

        public void Report(TimeSpan position)
        {
            lock (_lock)
            {
                _reportedPosition = position;
                _reportedAt = _wallClock.Elapsed;
            }
        }

        public void PauseWallClock() => _wallClock.Stop();
        public void ResumeWallClock() => _wallClock.Start();
    }
}
