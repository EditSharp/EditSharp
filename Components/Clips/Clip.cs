using System;
using System.Linq;
using EditSharp.Components.Channels;
using EditSharp.Components.Nodes;
using EditSharp.History;

using EditSharp.Editing;

namespace EditSharp.Components.Clips
{
    /// <summary>A span of a channel that shows or plays its <see cref="Graph"/>.</summary>
    /// <remarks>
    /// What a clip shows or plays comes from the input nodes in its graph, so the kind
    /// of content is the kind of source, not the kind of clip; <see cref="VideoClip"/>
    /// and <see cref="AudioClip"/> are the only two. Edits that can reach neighbouring
    /// clips (moving, extending, stretching, splitting, deleting) go through the
    /// clip's <see cref="Channel"/>, which keeps clips from overlapping.
    /// </remarks>
    public abstract class Clip : ITimelineEditable
    {
        string _name = "Clip";
        /// <summary>The clip's name, as editors show it.</summary>
        [Editable("Name", Order = 0)]
        public string Name { get => _name; set => Transaction.Set(this, ref _name, value, static (o, v) => o._name = v); }

        TimeSpan _start;
        /// <summary>Where the clip starts on the timeline.</summary>
        /// <remarks>To move a placed clip, use <see cref="Move"/>; setting Start directly doesn't update its channel.</remarks>
        [Editable("Start", Order = 1)]
        public TimeSpan Start { get => _start; set => Transaction.Set(this, ref _start, value, static (o, v) => o._start = v); }
        TimeSpan _duration;
        /// <summary>How long the clip lasts on the timeline.</summary>
        /// <remarks>To resize a placed clip, use the trim, extend and stretch methods; setting Duration directly doesn't check its neighbours or sources.</remarks>
        [Editable("Duration", Order = 2)]
        public TimeSpan Duration { get => _duration; set => Transaction.Set(this, ref _duration, value, static (o, v) => o._duration = v); }
        /// <summary>Where the clip ends on the timeline: <see cref="Start"/> + <see cref="Duration"/>.</summary>
        public TimeSpan End => Start + Duration;

        double _speed = 1d;
        /// <summary>How fast the content plays against the timeline: 2 plays it twice as fast, 0.5 at half speed.</summary>
        /// <remarks>The graph and its keyframes stay at 1x; Speed scales the clip's time wherever it's evaluated, so setting it back to 1 undoes a retime. <see cref="StretchStart"/> and <see cref="StretchEnd"/> set it. Raising it trims the clip if a source now ends inside it.</remarks>
        [Editable("Speed", Order = 3, Min = 0.01, Max = 100, Step = 0.01, Editor = PropertyEditor.Percent, Default = 1.0)]
        public double Speed { get => _speed; set { Transaction.Set(this, ref _speed, value, static (o, v) => o._speed = v); TrimToSources(); } }

        /// <summary>How much content the clip covers: <see cref="Duration"/> × <see cref="Speed"/>.</summary>
        public TimeSpan ContentDuration => ToContentTime(Duration);

        internal TimeSpan ToContentTime(TimeSpan timeline) =>
            TimeSpan.FromTicks((long)Math.Round(timeline.Ticks * Speed));

        internal TimeSpan ToTimelineTime(TimeSpan content)
        {
            double ticks = content.Ticks / Speed;
            return ticks >= long.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)Math.Round(ticks));
        }

        Guid? _linkGroupId;
        /// <summary>The <see cref="LinkGroup"/> the clip belongs to; null when it isn't linked.</summary>
        public Guid? LinkGroupId { get => _linkGroupId; internal set => Transaction.Set(this, ref _linkGroupId, value, static (o, v) => o._linkGroupId = v); }

        Channel? _channel;
        /// <summary>The channel the clip is on; null when it isn't placed.</summary>
        public Channel? Channel { get => _channel; internal set => Transaction.Set(this, ref _channel, value, static (o, v) => o._channel = v); }

        /// <summary>The shortest a trim or stretch leaves a clip: 1 ms.</summary>
        public static readonly TimeSpan MinimumDuration = TimeSpan.FromMilliseconds(1);

        /// <summary>The clip's content and effects.</summary>
        public abstract Graph Graph { get; }

        /// <summary>A deep copy with the same name, times and speed, not placed and not linked.</summary>
        /// <remarks>Nothing is recorded in history.</remarks>
        /// <returns>The copy.</returns>
        public abstract Clip Duplicate();

        /// <summary>Moves every trimmable input's in-point, and every keyframe in the graph, when the clip's start moves.</summary>
        /// <remarks>Keyframes belong to the content, not to <see cref="Start"/>, so a head trim or extend moves them too.</remarks>
        /// <param name="amount">How far the in-points move, in content time: positive for a trim, negative for an extend.</param>
        protected internal virtual void OnHeadInPointShift(TimeSpan amount)
        {
            foreach (ITrimmableInput trimmable in Graph.AllNodes.OfType<ITrimmableInput>())
                trimmable.InPoint += amount;

            //the head moving later puts every keyframe that much earlier relative to the new start;
            //generator content included, since its tint keyframes still have to stay where they were
            foreach (IAnimatable animatable in Graph.Animatables) animatable.ShiftKeyframes(-amount);
        }

        /// <summary>How far the start can move earlier, in content time: the least headroom of any trimmable input.</summary>
        /// <returns>The headroom; <see cref="TimeSpan.MaxValue"/> when no input limits it.</returns>
        protected internal virtual TimeSpan MaxHeadExtend()
        {
            TimeSpan min = TimeSpan.MaxValue;

            foreach (ITrimmableInput trimmable in Graph.AllNodes.OfType<ITrimmableInput>())
            {
                if (trimmable.MaxHeadroom < min) min = trimmable.MaxHeadroom;
            }

            return min;
        }

        /// <summary>How much earlier <see cref="Start"/> can move before a source runs out, in timeline time; <see cref="TimeSpan.MaxValue"/> when nothing limits it.</summary>
        /// <remarks><see cref="ExtendStart"/> stops here; an editor can clamp a drag preview to it.</remarks>
        public TimeSpan HeadExtendLimit
        {
            get
            {
                TimeSpan ceiling = MaxHeadExtend();
                return ceiling == TimeSpan.MaxValue ? ceiling : ToTimelineTime(ceiling);
            }
        }

        /// <summary>How much later <see cref="End"/> can move before a source runs out, in timeline time.</summary>
        /// <remarks>Zero when a source already ends inside the clip; <see cref="TimeSpan.MaxValue"/> when no source has a known end (it has none, it loops, or it isn't probed yet).</remarks>
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

        //after an edit (a source's Duration, in-point, Loop or file; the clip's Speed), trims the end back to a
        //source's end the clip now runs past, in the same undo step. Placed clips only; never while loading,
        //copying or replaying history. Under a millisecond is rounding
        internal void TrimToSources()
        {
            if (Channel is null || Transaction.IsSuppressed || Transaction.IsReplaying) return;

            if (SourceRoom() is { } room && room < -TimeSpan.FromMilliseconds(1))
                TrimEnd(ToTimelineTime(-room));
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public void ExtendStart(TimeSpan amount) => ExtendStartCore(amount, ripple: false);
        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public void ExtendEnd(TimeSpan amount) => ExtendEndCore(amount, ripple: false);
        /// <inheritdoc/>
        public void RippleExtendEnd(TimeSpan amount) => ExtendEndCore(amount, ripple: true);

        private void ExtendEndCore(TimeSpan amount, bool ripple)
        {
            TimeSpan limit = TailExtendLimit;
            if (amount > limit) amount = limit;
            if (amount <= TimeSpan.Zero) return;

            RequireChannel().ExtendTail(this, amount, ripple);
        }

        //the change itself, once the channel has cleared the way
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

        /// <summary>Moves the start while the end and the content stay put, so the clip plays slower or faster.</summary>
        /// <remarks>Growing overwrites whatever the clip grows into; <see cref="Speed"/> takes up the change.</remarks>
        /// <param name="amount">Positive moves the start earlier (longer, slower); negative later (shorter, faster). It stops at the timeline's start and at <see cref="MinimumDuration"/>.</param>
        /// <exception cref="InvalidOperationException">The clip isn't placed.</exception>
        public void StretchStart(TimeSpan amount)
        {
            amount = ClampStretch(amount);
            // the head cannot go before the start of the timeline
            if (amount > Start) amount = Start;
            if (amount == TimeSpan.Zero) return;

            RequireChannel().StretchHead(this, amount);
        }

        /// <summary>Moves the end while the start and the content stay put, so the clip plays slower or faster.</summary>
        /// <remarks>Growing overwrites whatever the clip grows into; <see cref="Speed"/> takes up the change.</remarks>
        /// <param name="amount">Positive moves the end later (longer, slower); negative earlier (shorter, faster). It stops at <see cref="MinimumDuration"/>.</param>
        /// <exception cref="InvalidOperationException">The clip isn't placed.</exception>
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

        //the change itself, once the channel has cleared the way; the content range is held and Speed takes up the new Duration
        internal void ApplyStretch(TimeSpan newStart, TimeSpan newDuration)
        {
            TimeSpan content = ContentDuration;

            Start = newStart;
            Duration = newDuration;

            if (newDuration > TimeSpan.Zero) Speed = (double)content.Ticks / newDuration.Ticks;
        }

        /// <inheritdoc/>
        public void Move(TimeSpan newStart, Channel? targetChannel = null) =>
            RequireChannel().Move(this, newStart, targetChannel, ripple: false);

        /// <inheritdoc/>
        public void RippleMove(TimeSpan newStart, Channel? targetChannel = null) =>
            RequireChannel().Move(this, newStart, targetChannel, ripple: true);

        /// <inheritdoc/>
        public void Split(TimeSpan at) => RequireChannel().SplitClip(this, at);

        /// <inheritdoc/>
        public void Delete() => RequireChannel().RemoveClip(this);

        /// <summary>Deletes the clip and closes the gap: everything after it on its channel moves earlier by its length.</summary>
        /// <exception cref="InvalidOperationException">The clip isn't placed.</exception>
        public void RippleDelete() => RequireChannel().RippleRemoveRange(Start, End);

        private Channel RequireChannel() =>
            Channel ?? throw new InvalidOperationException("This clip is not currently placed on any Channel.");

        /// <summary>Whether the clip starts at a time.</summary>
        /// <param name="time">The timeline time.</param>
        /// <returns>True when <see cref="Start"/> is <paramref name="time"/>.</returns>
        public bool StartsAt(TimeSpan time) => Start == time;
        /// <summary>Whether the clip ends at a time.</summary>
        /// <param name="time">The timeline time.</param>
        /// <returns>True when <see cref="End"/> is <paramref name="time"/>.</returns>
        public bool EndsAt(TimeSpan time) => End == time;

        /// <summary>Whether the clip starts strictly inside another.</summary>
        /// <param name="other">The other clip.</param>
        /// <returns>True when <see cref="Start"/> is after the other's start and before its end.</returns>
        public bool StartsInside(Clip other) => Start > other.Start && Start < other.End;
        /// <summary>Whether the clip ends strictly inside another.</summary>
        /// <param name="other">The other clip.</param>
        /// <returns>True when <see cref="End"/> is after the other's start and before its end.</returns>
        public bool EndsInside(Clip other) => End > other.Start && End < other.End;

        /// <summary>Whether the clip starts within another, counting its start.</summary>
        /// <param name="other">The other clip.</param>
        /// <returns>True when <see cref="Start"/> is at or after the other's start and before its end.</returns>
        public bool StartIntersectsWith(Clip other) => Start >= other.Start && Start < other.End;
        /// <summary>Whether the clip ends within another, counting its end.</summary>
        /// <param name="other">The other clip.</param>
        /// <returns>True when <see cref="End"/> is after the other's start and at or before its end.</returns>
        public bool EndIntersectsWith(Clip other) => End > other.Start && End <= other.End;

        /// <summary>Whether the clip covers all of another.</summary>
        /// <param name="other">The other clip.</param>
        /// <returns>True when the clip starts at or before the other and ends at or after it.</returns>
        public bool FullyIntersects(Clip other) => Start <= other.Start && End >= other.End;

        /// <summary>Whether the clip is longer than another.</summary>
        /// <param name="other">The other clip.</param>
        /// <returns>True when its <see cref="Duration"/> is greater.</returns>
        public bool IsLongerThan(Clip other) => Duration > other.Duration;

        /// <summary>How long the clip and another overlap.</summary>
        /// <param name="other">The other clip.</param>
        /// <returns>The overlap; zero when they don't overlap.</returns>
        public TimeSpan IntersectionWith(Clip other)
        {
            TimeSpan start = Start > other.Start ? Start : other.Start;
            TimeSpan end = End < other.End ? End : other.End;
            return end > start ? end - start : TimeSpan.Zero;
        }

        /// <summary>The gap between the clip and another.</summary>
        /// <param name="other">The other clip.</param>
        /// <returns>The time between the end of the earlier clip and the start of the later; zero when they overlap.</returns>
        public TimeSpan DistanceFrom(Clip other)
        {
            if (IntersectionWith(other) > TimeSpan.Zero) return TimeSpan.Zero;

            if (End < other.Start)
            {
                return other.Start - End;
            }
            else
            {
                return Start - other.End;
            }
        }
    }
}
