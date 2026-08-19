using System;
using System.Threading;

namespace EditSharp.Playback
{
    /// <summary>
    /// Publishes "where the LEADER stream currently is" (absolute timeline
    /// position) so a FOLLOWER stream can pace itself against the leader's
    /// actual delivered position instead of its own independent Stopwatch.
    /// Which stream leads vs follows is decided once per session, from
    /// PlaybackMode — see Playback's own remarks for the full mapping.
    ///
    /// Lock-free: backed by a single long (TimeSpan.Ticks) read/written via
    /// Interlocked. Written by at most one loop, read by at most one other,
    /// both far more often (per-frame, per-chunk) than a lock would
    /// comfortably support.
    ///
    /// Seeded with the session's start position in Play() before either
    /// loop begins, so a follower's very first check (against frame/chunk
    /// zero) reads a sane value even before the leader has reported
    /// anything real yet.
    /// </summary>
    internal sealed class PlaybackReferenceClock
    {
        // How often a follower re-checks the leader's position while
        // waiting for it to catch up. Small enough to feel responsive,
        // large enough not to spin. This directly trades off against
        // follower latency: a follower can be delayed up to PollInterval
        // beyond when the leader actually became due, since it only
        // notices on its next check rather than computing an exact wait
        // the way a leader's own Stopwatch-based delay does.
        public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(2);

        private long _positionTicks;

        public TimeSpan Position => TimeSpan.FromTicks(Interlocked.Read(ref _positionTicks));

        public void Report(TimeSpan position)
        {
            Interlocked.Exchange(ref _positionTicks, position.Ticks);
        }
    }
}
