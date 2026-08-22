using System;
using System.Linq;
using EditSharp.Components.Nodes;
 
namespace EditSharp.Components.Clips
{
    /// <summary>
    /// FUNDAMENTAL REWRITE ("clips are graphs"): a clip used to be treated
    /// as "media that can have a graph of effects" — a Clip subtype per
    /// kind of content (VideoClip/TextClip/GeneratorClip/NoiseClip/
    /// TimelineVideoClip on the visual side; AudioClip/TimelineAudioClip on
    /// the audible side), each wrapping its own bespoke content field, with
    /// a Graph bolted on afterward. That was backwards: a clip IS
    /// its graph. The only remaining concrete Clip subtypes are VideoClip
    /// and AudioClip — everything that used to be a distinct Clip subtype
    /// (a media file, text, a generated color, procedural noise, a
    /// synthesized tone, an embedded nested Timeline) is now just a
    /// different InputNode wired into an otherwise perfectly ordinary
    /// Graph (see EditSharp.Components.Nodes).
    /// A graph may have more than one InputNode — nothing about a graph
    /// having two, three, or more requires a different kind of Clip; it's
    /// still just VideoClip or AudioClip.
    ///
    /// Abstract base, implements ITimelineEditable.
    /// </summary>
    public abstract class Clip : ITimelineEditable
    {
        public TimeSpan Start { get; set; }
        public TimeSpan Duration { get; set; }
        public TimeSpan End => Start + Duration;
 
        //null = unlinked
        public Guid? LinkGroupId { get; internal set; }
 
        //back-ref, set by the owning Channel
        public Channel? Channel { get; internal set; }
 
        internal static readonly TimeSpan MinimumDuration = TimeSpan.FromMilliseconds(1);
 
        /// <summary>
        /// A clip's single Graph, fixed to this clip's own
        /// NodeDomain at construction (Image for VideoClip, Audio for
        /// AudioClip) — see this class's own remarks.
        /// </summary>
        public abstract Graph Graph { get; }
 
        /// <summary>
        /// Deep copy — does NOT copy LinkGroupId/Channel (a duplicate
        /// starts unlinked and unplaced; the caller decides where it goes).
        /// </summary>
        public abstract Clip Duplicate();
 
        // ---------------------------------------------------------------
        // Head in-point shifting — now GENERIC over every trimmable input
        // node in this clip's graph, rather than a per-Clip-subtype
        // override. See ITrimmableInput's own remarks and the schema doc's
        // multi-input-trim rule: every trimmable input shifts together, by
        // the same amount.
        // ---------------------------------------------------------------
 
        /// <summary>Positive `amount` = trim (in-point advances), negative = extend (in-point recedes).</summary>
        protected internal virtual void OnHeadInPointShift(TimeSpan amount)
        {
            foreach (ITrimmableInput trimmable in Graph.Nodes.OfType<ITrimmableInput>())
                trimmable.InPoint += amount;
        }
 
        /// <summary>
        /// The most-constrained trimmable input node sets the ceiling for
        /// the WHOLE clip — extending the head further than any ONE of them
        /// has room for would push that node's in-point negative.
        /// TimeSpan.MaxValue (unconstrained) when there are no trimmable
        /// inputs at all in this clip's graph (pure generator/text/noise/
        /// tone content) — matches the old TextClip/GeneratorClip/
        /// NoiseClip's inherited unbounded default.
        /// </summary>
        protected internal virtual TimeSpan MaxHeadExtend()
        {
            TimeSpan min = TimeSpan.MaxValue;
 
            foreach (ITrimmableInput trimmable in Graph.Nodes.OfType<ITrimmableInput>())
            {
                if (trimmable.MaxHeadroom < min) min = trimmable.MaxHeadroom;
            }
 
            return min;
        }
 
        // ---------------------------------------------------------------
        // Trim — self-contained, never touches sibling clips (trimming can
        // never create an overlap, only shrink this clip's own span).
        // ---------------------------------------------------------------
 
        public void TrimStart(TimeSpan amount)
        {
            TimeSpan clamped = ClampTrim(amount);
            if (clamped <= TimeSpan.Zero) return;
 
            Start += clamped;
            Duration -= clamped;
            OnHeadInPointShift(clamped);
            Channel?.ReconcileTransitionsFor(this);
        }
 
        public void TrimEnd(TimeSpan amount)
        {
            TimeSpan clamped = ClampTrim(amount);
            if (clamped <= TimeSpan.Zero) return;
 
            Duration -= clamped;
            Channel?.ReconcileTransitionsFor(this);
        }
 
        private TimeSpan ClampTrim(TimeSpan amount)
        {
            if (amount <= TimeSpan.Zero) return TimeSpan.Zero;
 
            TimeSpan maxTrim = Duration - MinimumDuration;
            if (maxTrim < TimeSpan.Zero) maxTrim = TimeSpan.Zero;
 
            return amount > maxTrim ? maxTrim : amount;
        }
 
        // ---------------------------------------------------------------
        // Extend — may collide with a sibling clip, so conflict resolution
        // (Overwrite/Ripple) is delegated to Channel.
        // ---------------------------------------------------------------
 
        public void ExtendStart(TimeSpan amount) => ExtendStartCore(amount, ripple: false);
        public void RippleExtendStart(TimeSpan amount) => ExtendStartCore(amount, ripple: true);
 
        private void ExtendStartCore(TimeSpan amount, bool ripple)
        {
            if (amount <= TimeSpan.Zero) return;
 
            TimeSpan clamped = ClampToContentCeiling(amount);
            if (clamped <= TimeSpan.Zero) return;
 
            RequireChannel().ExtendHead(this, clamped, ripple);
        }
 
        private TimeSpan ClampToContentCeiling(TimeSpan amount)
        {
            TimeSpan ceiling = MaxHeadExtend();
            return ceiling == TimeSpan.MaxValue || amount <= ceiling ? amount : ceiling;
        }
 
        public void ExtendEnd(TimeSpan amount) => ExtendEndCore(amount, ripple: false);
        public void RippleExtendEnd(TimeSpan amount) => ExtendEndCore(amount, ripple: true);
 
        private void ExtendEndCore(TimeSpan amount, bool ripple)
        {
            //no content-ceiling clamp on the tail side — freeze-frame/hold-
            //last-sample covers every input-node type past its own end, so
            //the tail is always extendable
            if (amount <= TimeSpan.Zero) return;
 
            RequireChannel().ExtendTail(this, amount, ripple);
        }
 
        /// <summary>Actual mutation once Channel has resolved any conflicts with siblings.</summary>
        internal void ApplyHeadExtend(TimeSpan amount)
        {
            Start -= amount;
            Duration += amount;
            OnHeadInPointShift(-amount);
        }
 
        internal void ApplyTailExtend(TimeSpan amount)
        {
            Duration += amount;
        }
 
        // ---------------------------------------------------------------
        // Move / Split / Delete — all delegate to Channel, the sole
        // authority on the no-overlap invariant and conflict resolution.
        // ---------------------------------------------------------------
 
        public void Move(TimeSpan newStart, Channel? targetChannel = null) =>
            RequireChannel().Move(this, newStart, targetChannel, ripple: false);
 
        public void RippleMove(TimeSpan newStart, Channel? targetChannel = null) =>
            RequireChannel().Move(this, newStart, targetChannel, ripple: true);
 
        public void Split(TimeSpan at) => RequireChannel().SplitClip(this, at);
 
        public void Delete() => RequireChannel().RemoveClip(this);
 
        private Channel RequireChannel() =>
            Channel ?? throw new InvalidOperationException("This clip is not currently placed on any Channel.");
 
        // ---------------------------------------------------------------
        // Relative-position helpers
        // ---------------------------------------------------------------
 
        public bool StartsAt(TimeSpan time) => Start == time;
        public bool EndsAt(TimeSpan time) => End == time;
 
        public bool StartsInside(Clip other) => Start > other.Start && Start < other.End;
        public bool EndsInside(Clip other) => End > other.Start && End < other.End;
 
        public bool StartIntersectsWith(Clip other) => Start >= other.Start && Start < other.End;
        public bool EndIntersectsWith(Clip other) => End > other.Start && End <= other.End;
 
        public bool FullyIntersects(Clip other) => Start <= other.Start && End >= other.End;
 
        public bool IsLongerThan(Clip other) => Duration > other.Duration;
 
        public TimeSpan IntersectionWith(Clip other)
        {
            TimeSpan start = Start > other.Start ? Start : other.Start;
            TimeSpan end = End < other.End ? End : other.End;
            return end > start ? end - start : TimeSpan.Zero;
        }
    }
}
 