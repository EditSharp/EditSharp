using System;
using System.Linq;
using EditSharp.Components.Channels;
using EditSharp.Components.Nodes;
using EditSharp.History;

using EditSharp.Editing;
 
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
        string _name = "Clip";
        [Editable("Name", Order = 0)]
        public string Name { get => _name; set => Transaction.Set(this, ref _name, value, static (o, v) => o._name = v); }

        TimeSpan _start;
        [Editable("Start", Order = 1)]
        public TimeSpan Start { get => _start; set => Transaction.Set(this, ref _start, value, static (o, v) => o._start = v); }
        TimeSpan _duration;
        [Editable("Duration", Order = 2)]
        public TimeSpan Duration { get => _duration; set => Transaction.Set(this, ref _duration, value, static (o, v) => o._duration = v); }
        public TimeSpan End => Start + Duration;

        /// <summary>
        /// How fast this clip's content plays against the timeline, in
        /// content seconds per timeline second: 1 is real time, 2 covers
        /// the same content in half the Duration, 0.5 in twice. The graph
        /// and its keyframes are stored at 1x; this is a multiplier on the
        /// clip's local time everywhere it is evaluated (FrameStateResolver
        /// for video, AudioMixer/AudioGraphEvaluator for audio), so setting
        /// it back to 1 undoes a retime completely. Written by StretchStart/
        /// StretchEnd, which change Duration while holding the content range
        /// fixed; every in-point shift here is scaled through it so a trim
        /// on a retimed clip still lands on the right content.
        /// </summary>
        double _speed = 1d;
        [Editable("Speed", Order = 3, Min = 0.01, Max = 100, Step = 0.01, Editor = PropertyEditor.Percent, Default = 1.0)]
        public double Speed { get => _speed; set { Transaction.Set(this, ref _speed, value, static (o, v) => o._speed = v); TrimToSources(); } }

        /// <summary>The span of content this clip covers — Duration scaled by Speed.</summary>
        public TimeSpan ContentDuration => ToContentTime(Duration);

        internal TimeSpan ToContentTime(TimeSpan timeline) =>
            TimeSpan.FromTicks((long)Math.Round(timeline.Ticks * Speed));

        internal TimeSpan ToTimelineTime(TimeSpan content)
        {
            double ticks = content.Ticks / Speed;
            return ticks >= long.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)Math.Round(ticks));
        }
 
        //null = unlinked
        Guid? _linkGroupId;
        public Guid? LinkGroupId { get => _linkGroupId; internal set => Transaction.Set(this, ref _linkGroupId, value, static (o, v) => o._linkGroupId = v); }
 
        //back-ref, set by the owning Channel
        Channel? _channel;
        public Channel? Channel { get => _channel; internal set => Transaction.Set(this, ref _channel, value, static (o, v) => o._channel = v); }
 
        public static readonly TimeSpan MinimumDuration = TimeSpan.FromMilliseconds(1);
 
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
            foreach (ITrimmableInput trimmable in Graph.AllNodes.OfType<ITrimmableInput>())
                trimmable.InPoint += amount;

            //keyframes are anchored to the content, not to Start: the head
            //moving later by `amount` puts every keyframe `amount` earlier
            //relative to the new start, and an extend does the reverse.
            //this applies to every clip, generator content included — a
            //noise clip has no in-point, but its tint keyframes still have
            //to stay where they were
            foreach (IAnimatable animatable in Graph.Animatables) animatable.ShiftKeyframes(-amount);
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
 
            foreach (ITrimmableInput trimmable in Graph.AllNodes.OfType<ITrimmableInput>())
            {
                if (trimmable.MaxHeadroom < min) min = trimmable.MaxHeadroom;
            }
 
            return min;
        }
 
        // ---------------------------------------------------------------
        // Trim — self-contained, never touches sibling clips (trimming can
        // never create an overlap, only shrink this clip's own span).
        // ---------------------------------------------------------------
 
        /// <summary>
        /// How much earlier Start can be pulled before the content runs
        /// out, in TIMELINE time (MaxHeadExtend is content time, and the
        /// two differ once Speed is not 1). TimeSpan.MaxValue when nothing
        /// in the graph constrains it. Exposed so an editor can clamp a
        /// preview to what ExtendStart will actually accept.
        /// </summary>
        public TimeSpan HeadExtendLimit
        {
            get
            {
                TimeSpan ceiling = MaxHeadExtend();
                return ceiling == TimeSpan.MaxValue ? ceiling : ToTimelineTime(ceiling);
            }
        }

        /// <summary>
        /// How much later End can be pushed before a source runs out, in
        /// TIMELINE time: the tightest of the trimmable inputs with a known
        /// hard end, zero if one already ends inside the clip. TimeSpan.MaxValue
        /// when none has one (unbounded, looping, or not probed yet).
        /// </summary>
        public TimeSpan TailExtendLimit => SourceRoom() is not { } r ? TimeSpan.MaxValue
            : r <= TimeSpan.Zero ? TimeSpan.Zero : ToTimelineTime(r);

        //content left past the clip's end in its tightest source with a known hard end; negative when the clip runs past one
        private TimeSpan? SourceRoom()
        {
            TimeSpan? room = null;

            foreach (ITrimmableInput trimmable in Graph.AllNodes.OfType<ITrimmableInput>())
            {
                if (trimmable.ContentLength is not { } length) continue;
                TimeSpan left = length - ContentDuration;
                if (room is null || left < room) room = left;
            }

            return room;
        }

        /// <summary>
        /// After an edit (a source's Duration, in-point, Loop, file; the clip's
        /// Speed): trims the end back to a source's hard end the clip now runs
        /// past, in the same undo step. Placed clips only; never while loading,
        /// copying or replaying history. Under a millisecond is rounding.
        /// </summary>
        internal void TrimToSources()
        {
            if (Channel is null || Transaction.IsSuppressed || Transaction.IsReplaying) return;

            if (SourceRoom() is { } room && room < -TimeSpan.FromMilliseconds(1))
                TrimEnd(ToTimelineTime(-room));
        }

        public void TrimStart(TimeSpan amount)
        {
            TimeSpan clamped = ClampTrim(amount);
            if (clamped <= TimeSpan.Zero) return;
 
            TimeSpan previousStart = Start;
            Start += clamped;
            Duration -= clamped;
            OnHeadInPointShift(ToContentTime(clamped));
            Channel?.Rekey(this, previousStart);
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
            TimeSpan ceiling = HeadExtendLimit;
            return ceiling == TimeSpan.MaxValue || amount <= ceiling ? amount : ceiling;
        }
 
        public void ExtendEnd(TimeSpan amount) => ExtendEndCore(amount, ripple: false);
        public void RippleExtendEnd(TimeSpan amount) => ExtendEndCore(amount, ripple: true);
 
        private void ExtendEndCore(TimeSpan amount, bool ripple)
        {
            TimeSpan limit = TailExtendLimit;
            if (amount > limit) amount = limit;
            if (amount <= TimeSpan.Zero) return;

            RequireChannel().ExtendTail(this, amount, ripple);
        }
 
        /// <summary>Actual mutation once Channel has resolved any conflicts with siblings.</summary>
        internal void ApplyHeadExtend(TimeSpan amount)
        {
            Start -= amount;
            Duration += amount;
            OnHeadInPointShift(-ToContentTime(amount));
        }
 
        internal void ApplyTailExtend(TimeSpan amount)
        {
            Duration += amount;
        }

        // ---------------------------------------------------------------
        // Stretch — retime. One end of the clip moves while the other end
        // and the content range stay put, so Speed absorbs the difference.
        // Growing can collide with a sibling exactly like Extend, so the
        // Channel resolves that (Overwrite) before the mutation lands.
        // ---------------------------------------------------------------

        /// <summary>Positive `amount` = Start moves earlier (clip grows, plays slower); negative = later (shrinks, plays faster). End never moves.</summary>
        public void StretchStart(TimeSpan amount)
        {
            amount = ClampStretch(amount);
            // the head cannot go before the start of the timeline
            if (amount > Start) amount = Start;
            if (amount == TimeSpan.Zero) return;

            RequireChannel().StretchHead(this, amount);
        }

        /// <summary>Positive `amount` = End moves later (clip grows, plays slower); negative = earlier (shrinks, plays faster). Start never moves.</summary>
        public void StretchEnd(TimeSpan amount)
        {
            amount = ClampStretch(amount);
            if (amount == TimeSpan.Zero) return;

            RequireChannel().StretchTail(this, amount);
        }

        // a stretch may shrink the clip, but never below the minimum duration
        private TimeSpan ClampStretch(TimeSpan amount)
        {
            TimeSpan maxShrink = Duration - MinimumDuration;
            if (maxShrink < TimeSpan.Zero) maxShrink = TimeSpan.Zero;

            return amount < -maxShrink ? -maxShrink : amount;
        }

        /// <summary>Actual mutation once Channel has resolved any conflicts. The content range is held; Speed takes up the new Duration.</summary>
        internal void ApplyStretch(TimeSpan newStart, TimeSpan newDuration)
        {
            TimeSpan content = ContentDuration;

            Start = newStart;
            Duration = newDuration;

            if (newDuration > TimeSpan.Zero) Speed = (double)content.Ticks / newDuration.Ticks;
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

        /// <summary>Delete, and close the gap this clip leaves on its own channel.</summary>
        public void RippleDelete() => RequireChannel().RippleRemoveRange(Start, End);
 
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

        public TimeSpan DistanceFrom(Clip other) 
        {
            // if clips intersect at all, immediately return zero distance
            if (IntersectionWith(other) > TimeSpan.Zero) return TimeSpan.Zero;

            // this clip comes before the other clip
            if (End < other.Start) 
            {
                return other.Start - End;
            }
            // this clip comes after the other clip
            else 
            {
                return Start - other.End;
            }
        }
    }
}
 