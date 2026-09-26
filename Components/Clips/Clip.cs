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
    /// of content is the kind of input, not the kind of clip; <see cref="VideoClip"/>
    /// and <see cref="AudioClip"/> are the only two. Edits that can reach neighbouring
    /// clips (moving, extending, stretching, splitting, deleting) go through the
    /// clip's <see cref="Channel"/>, which keeps clips from overlapping.
    /// </remarks>
    public abstract class Clip : ITimelineEditable
    {
        /// <summary>Identifies the clip within its project; kept when it's saved and loaded, new for a copy.</summary>
        public Guid Id { get; internal set; } = Guid.NewGuid();

        string _name = "Clip";
        /// <summary>The clip's name, as editors show it.</summary>
        [Editable("Name", Order = 0)]
        public string Name { get => _name; set => Transaction.Set(this, ref _name, value, static (o, v) => o._name = v); }

        Time _start;
        /// <summary>Where the clip starts on the timeline.</summary>
        /// <remarks>To move a placed clip, use <see cref="Move"/>; setting Start directly doesn't update its channel.</remarks>
        [Editable("Start", Order = 1)]
        public Time Start { get => _start; set => Transaction.Set(this, ref _start, value, static (o, v) => o._start = v); }
        Time _duration;
        /// <summary>How long the clip lasts on the timeline.</summary>
        /// <remarks>To resize a placed clip, use the trim, extend and stretch methods; setting Duration directly doesn't check its neighbours or sources.</remarks>
        [Editable("Duration", Order = 2)]
        public Time Duration { get => _duration; set => Transaction.Set(this, ref _duration, value, static (o, v) => o._duration = v); }
        /// <summary>Where the clip ends on the timeline: <see cref="Start"/> + <see cref="Duration"/>.</summary>
        public Time End => Start + Duration;

        Rational _speed = Rational.One;
        /// <summary>How fast the content plays against the timeline: 2 plays it twice as fast, 1/2 at half speed, -1 backwards, 0 holds one frame.</summary>
        /// <remarks>
        /// The graph and its keyframes stay at 1x; Speed only maps the clip's time, so setting it back to 1 undoes a retime.
        /// Backwards, the clip plays the same stretch of content from its end, while keyframes on effects keep running
        /// forwards at |Speed|; <see cref="Reverse"/> turns them around too. At 0 the clip holds the frame at
        /// <see cref="FreezeAt"/> and its keyframes run at 1x. <see cref="StretchStart"/> and <see cref="StretchEnd"/> set the
        /// exact ratio of content to timeline ticks. A linked clip's partners take the same speed. Raising it trims the clip
        /// if a source now ends inside it.
        /// </remarks>
        [Editable("Speed", Order = 3, Group = "Speed", Step = 0.01, Editor = PropertyEditor.Percent, Default = 1.0)]
        [ReadOnlyWhen(nameof(Frozen), true)]
        public Rational Speed
        {
            get => _speed;
            set
            {
                foreach (Clip clip in LinkedClips()) clip.SetSpeed(value);
            }
        }

        //this clip's speed alone, remembering the last moving speed so a freeze can be undone
        private void SetSpeed(Rational value)
        {
            if (value == _speed) return;
            if (value.IsZero && !_speed.IsZero) SpeedBeforeFreeze = _speed;

            Transaction.Set(this, ref _speed, value, static (o, v) => o._speed = v);
            TrimToSources();
        }

        Rational _speedBeforeFreeze = Rational.One;
        /// <summary>The speed the clip had before it was last frozen, which turning <see cref="Frozen"/> off restores.</summary>
        public Rational SpeedBeforeFreeze
        {
            get => _speedBeforeFreeze;
            private set => Transaction.Set(this, ref _speedBeforeFreeze, value, static (o, v) => o._speedBeforeFreeze = v);
        }

        /// <summary>Whether the clip holds one frame: <see cref="Speed"/> is 0.</summary>
        /// <remarks>Turning it on sets the speed to 0 and keeps <see cref="FreezeAt"/>; turning it off restores <see cref="SpeedBeforeFreeze"/>. Use <see cref="Freeze"/> to hold the frame at a given moment.</remarks>
        [Editable("Freeze frame", Order = 4, Group = "Speed")]
        public bool Frozen
        {
            get => _speed.IsZero;
            set
            {
                if (value == Frozen) return;
                Speed = value ? Rational.Zero : SpeedBeforeFreeze;
            }
        }

        Time _freezeAt;
        /// <summary>The frame a frozen clip holds, as content time from the in-point.</summary>
        [Editable("Freeze at", Order = 5, Group = "Speed")]
        [VisibleWhen(nameof(Frozen), true)]
        public Time FreezeAt { get => _freezeAt; set => Transaction.Set(this, ref _freezeAt, value, static (o, v) => o._freezeAt = v); }

        /// <summary>Holds the frame showing at a moment on the timeline, for this clip and its linked partners.</summary>
        /// <param name="timelineTime">The moment; one outside the clip holds its first frame.</param>
        public void Freeze(Time timelineTime)
        {
            if (timelineTime < Start || timelineTime >= End) timelineTime = Start;

            foreach (Clip clip in LinkedClips())
            {
                if (!clip.Frozen) clip.FreezeAt = clip.MediaTimeAt(timelineTime);
            }

            Frozen = true;
        }

        /// <summary>Plays the clip backwards, turning its effects' keyframes around with it so they stay on the same frames; partners too. A frozen clip is left as it is.</summary>
        public void Reverse()
        {
            if (Frozen) return;

            foreach (Clip clip in LinkedClips())
            {
                //an animation time a lands where the frame it was on now shows: at ContentDuration - Tick - a
                Time around = clip.ContentDuration - Time.Tick;
                foreach (IAnimatable animatable in clip.EffectAnimatables) animatable.MirrorKeyframes(around);

                clip.SetSpeed(-clip._speed);
            }
        }

        //this clip and the others in its link group
        private System.Collections.Generic.IEnumerable<Clip> LinkedClips()
        {
            yield return this;

            if (LinkGroupId is not { } id || Channel?.Timeline is not { } timeline) yield break;

            foreach (Clip member in timeline.AllClips())
                if (member != this && member.LinkGroupId == id) yield return member;
        }

        /// <summary>Whether the clip plays backwards: <see cref="Speed"/> is negative.</summary>
        public bool IsReversed => _speed.IsNegative;

        string? _color;
        /// <summary>The colour an editor shows the clip in: a swatch name or a hex value, as the editor reads it; null for the editor's default.</summary>
        public string? Color { get => _color; set => Transaction.Set(this, ref _color, value, static (o, v) => o._color = v); }

        /// <summary>How much content the clip covers: <see cref="Duration"/> × |<see cref="Speed"/>|; zero when frozen.</summary>
        public Time ContentDuration => ToContentTime(Duration);

        /// <summary>A length of timeline time as content time: × |<see cref="Speed"/>|, rounded to the nearest tick.</summary>
        /// <param name="timeline">The timeline length.</param>
        /// <returns>The content length; zero when frozen.</returns>
        public Time ToContentTime(Time timeline) => timeline * _speed.Abs();

        /// <summary>A length of content time as timeline time: ÷ |<see cref="Speed"/>|, rounded to the nearest tick.</summary>
        /// <param name="content">The content length.</param>
        /// <returns>The timeline length; <see cref="Time.MaxValue"/> when it doesn't fit or the clip is frozen.</returns>
        public Time ToTimelineTime(Time content)
        {
            if (content == Time.MaxValue || _speed.IsZero) return Time.MaxValue;
            try { return content / _speed.Abs(); }
            catch (OverflowException) { return Time.MaxValue; }
        }

        /// <summary>The clip's animation time at a moment on the timeline: keyframes are placed and evaluated at this time.</summary>
        /// <param name="timelineTime">The timeline time.</param>
        /// <returns>(<paramref name="timelineTime"/> - <see cref="Start"/>) × |<see cref="Speed"/>|, or at 1x when frozen.</returns>
        public Time ContentTimeAt(Time timelineTime) => Frozen ? timelineTime - Start : ToContentTime(timelineTime - Start);

        /// <summary>Where on the timeline an animation time falls.</summary>
        /// <param name="contentTime">The animation time.</param>
        /// <returns>The timeline time.</returns>
        public Time TimelineTimeOf(Time contentTime) => Start + (Frozen ? contentTime : ToTimelineTime(contentTime));

        /// <summary>Which content the clip shows or plays at a moment on the timeline, as content time from the in-point.</summary>
        /// <param name="timelineTime">The timeline time.</param>
        /// <returns>Forwards the animation time; backwards the same distance back from the end of the covered content; frozen, <see cref="FreezeAt"/>.</returns>
        public Time MediaTimeAt(Time timelineTime)
        {
            if (Frozen) return FreezeAt;

            Time forward = ToContentTime(timelineTime - Start);
            return IsReversed ? Time.Max(Time.Zero, ContentDuration - Time.Tick - forward) : forward;
        }

        /// <summary>The animation time at which the clip shows a given content time; the inverse of <see cref="MediaTimeAt"/> in animation time.</summary>
        /// <param name="mediaTime">Content time from the in-point.</param>
        /// <returns>The animation time; zero when frozen.</returns>
        public Time ContentTimeOfMedia(Time mediaTime) => Frozen ? Time.Zero : IsReversed ? ContentDuration - Time.Tick - mediaTime : mediaTime;

        Guid? _linkGroupId;
        /// <summary>The <see cref="LinkGroup"/> the clip belongs to; null when it isn't linked.</summary>
        public Guid? LinkGroupId { get => _linkGroupId; internal set => Transaction.Set(this, ref _linkGroupId, value, static (o, v) => o._linkGroupId = v); }

        Channel? _channel;
        /// <summary>The channel the clip is on; null when it isn't placed.</summary>
        public Channel? Channel { get => _channel; internal set => Transaction.Set(this, ref _channel, value, static (o, v) => o._channel = v); }

        /// <summary>The shortest a trim or stretch leaves a clip: one tick.</summary>
        public static readonly Time MinimumDuration = Time.Tick;

        /// <summary>The clip's content and effects.</summary>
        public abstract Graph Graph { get; }

        /// <summary>A video clip and an audio clip for one media: its picture, and its soundtrack when it has one.</summary>
        /// <remarks>Neither clip is placed; add each to a channel, and link them with <see cref="Timeline.Link"/> to move as one. Nothing is recorded in history.</remarks>
        /// <param name="media">What the clips show and play.</param>
        /// <param name="start">Where the clips start on the timeline.</param>
        /// <param name="duration">How long they last.</param>
        /// <returns>The video clip, and the audio clip or null when the media has no audio.</returns>
        public static (VideoClip Video, AudioClip? Audio) CreateClipsFromMedia(Media.VideoMedia media, Time start, Time duration) => (
            VideoClip.CreateFromMedia(media, start, duration),
            media.Audio is { } audio ? AudioClip.CreateFromMedia(audio, start, duration) : null);

        /// <summary>A deep copy with the same name, times and speed, not placed and not linked.</summary>
        /// <remarks>Nothing is recorded in history.</remarks>
        /// <returns>The copy.</returns>
        public abstract Clip Duplicate();

        /// <summary>The keyframed values of the clip's effects: every node but its inputs, whose keyframes belong to the content.</summary>
        public System.Collections.Generic.IEnumerable<IAnimatable> EffectAnimatables =>
            Graph.AllNodes.Where(n => n is not InputNode).SelectMany(n => n.Animatables);

        /// <summary>Moves every trimmable input's in-point, keeping the inputs' own keyframes on their content.</summary>
        /// <param name="amount">How far the in-points move, in content time: positive later, negative earlier.</param>
        protected internal virtual void ShiftInPoints(Time amount)
        {
            foreach (InputNode trimmable in Graph.AllNodes.OfType<InputNode>())
            {
                trimmable.InPoint += amount;
                foreach (IAnimatable animatable in trimmable.Animatables) animatable.ShiftKeyframes(-amount);
            }
        }

        /// <summary>Moves the effects' keyframes against the clip's start, so they stay where they were on the timeline when the start moves.</summary>
        /// <param name="amount">How far to move them, in animation time.</param>
        protected internal virtual void ShiftEffectKeyframes(Time amount)
        {
            foreach (IAnimatable animatable in EffectAnimatables) animatable.ShiftKeyframes(amount);
        }

        //the head moving by `amount` of timeline time, later when positive: forwards the head is the start of the
        //content, backwards its end, and a frozen clip's content doesn't move at all. The effects' keyframes run from
        //the start, so they move the other way to stay put
        private void OnHeadMoved(Time amount)
        {
            Time content = amount < Time.Zero ? -ToContentTime(-amount) : ToContentTime(amount);

            if (!Frozen && !IsReversed) ShiftInPoints(content);
            ShiftEffectKeyframes(Frozen ? -amount : -content);
        }

        //the tail moving by `amount`, later when positive: only backwards does it reach the start of the content
        private void OnTailMoved(Time amount)
        {
            if (!IsReversed) return;

            Time content = amount < Time.Zero ? -ToContentTime(-amount) : ToContentTime(amount);
            ShiftInPoints(-content);
        }

        /// <summary>How far the start can move earlier, in content time: the least headroom of any trimmable input.</summary>
        /// <returns>The headroom; <see cref="Time.MaxValue"/> when no input limits it.</returns>
        protected internal virtual Time MaxHeadExtend()
        {
            Time min = Time.MaxValue;

            foreach (InputNode trimmable in Graph.AllNodes.OfType<InputNode>())
            {
                if (trimmable.MaxHeadroom < min) min = trimmable.MaxHeadroom;
            }

            return min;
        }

        /// <summary>How much earlier <see cref="Start"/> can move before a source runs out, in timeline time; <see cref="Time.MaxValue"/> when nothing limits it.</summary>
        /// <remarks><see cref="ExtendStart"/> stops here; an editor can clamp a drag preview to it. Backwards the head reaches for content after the covered stretch; a frozen clip has no limit.</remarks>
        public Time HeadExtendLimit => Frozen ? Time.MaxValue : IsReversed ? RoomAfter() : RoomBefore();

        /// <summary>How much later <see cref="End"/> can move before a source runs out, in timeline time.</summary>
        /// <remarks>Zero when a source already ends inside the clip; <see cref="Time.MaxValue"/> when no source has a known end (it has none, it loops, or it isn't probed yet), or the clip is frozen.</remarks>
        public Time TailExtendLimit => Frozen ? Time.MaxValue : IsReversed ? RoomBefore() : RoomAfter();

        //timeline time of content before the in-point
        private Time RoomBefore()
        {
            Time ceiling = MaxHeadExtend();
            return ceiling == Time.MaxValue ? ceiling : ToTimelineTime(ceiling);
        }

        //timeline time of content after the covered stretch
        private Time RoomAfter() => SourceRoom() is not { } r ? Time.MaxValue
            : r <= Time.Zero ? Time.Zero : ToTimelineTime(r);

        //content left past the clip's end in its tightest source with a known hard end; negative when the clip runs past one
        private Time? SourceRoom()
        {
            Time? room = null;

            foreach (InputNode trimmable in Graph.AllNodes.OfType<InputNode>())
            {
                if (trimmable.ContentLength is not { } length) continue;
                Time left = length - ContentDuration;
                if (room is null || left < room) room = left;
            }

            return room;
        }

        //after an edit (a source's Duration, in-point, Loop or file; the clip's Speed), trims the end back to a
        //source's end the clip now runs past, in the same undo step. Placed clips only; never while loading,
        //copying or replaying history
        internal void TrimToSources()
        {
            if (Channel is null || Transaction.IsSuppressed || Transaction.IsReplaying) return;

            if (Frozen || SourceRoom() is not { } room || room >= Time.Zero) return;

            //the content's end is the clip's end forwards, its head backwards
            if (IsReversed) TrimStart(ToTimelineTime(-room));
            else TrimEnd(ToTimelineTime(-room));
        }

        /// <inheritdoc/>
        public void TrimStart(Time amount)
        {
            Time clamped = ClampTrim(amount);
            if (clamped <= Time.Zero) return;

            Time previousStart = Start;
            Start += clamped;
            Duration -= clamped;
            OnHeadMoved(clamped);
            Channel?.Rekey(this, previousStart);
            Channel?.ReconcileTransitionsFor(this);
        }

        /// <inheritdoc/>
        public void TrimEnd(Time amount)
        {
            Time clamped = ClampTrim(amount);
            if (clamped <= Time.Zero) return;

            Duration -= clamped;
            OnTailMoved(-clamped);
            Channel?.ReconcileTransitionsFor(this);
        }

        private Time ClampTrim(Time amount)
        {
            if (amount <= Time.Zero) return Time.Zero;

            Time maxTrim = Duration - MinimumDuration;
            if (maxTrim < Time.Zero) maxTrim = Time.Zero;

            return amount > maxTrim ? maxTrim : amount;
        }

        /// <inheritdoc/>
        public void ExtendStart(Time amount) => ExtendStartCore(amount, ripple: false);
        /// <inheritdoc/>
        public void RippleExtendStart(Time amount) => ExtendStartCore(amount, ripple: true);

        private void ExtendStartCore(Time amount, bool ripple)
        {
            if (amount <= Time.Zero) return;

            Time clamped = ClampToContentCeiling(amount);
            if (clamped <= Time.Zero) return;

            RequireChannel().ExtendHead(this, clamped, ripple);
        }

        private Time ClampToContentCeiling(Time amount)
        {
            Time ceiling = HeadExtendLimit;
            return ceiling == Time.MaxValue || amount <= ceiling ? amount : ceiling;
        }

        /// <inheritdoc/>
        public void ExtendEnd(Time amount) => ExtendEndCore(amount, ripple: false);
        /// <inheritdoc/>
        public void RippleExtendEnd(Time amount) => ExtendEndCore(amount, ripple: true);

        private void ExtendEndCore(Time amount, bool ripple)
        {
            Time limit = TailExtendLimit;
            if (amount > limit) amount = limit;
            if (amount <= Time.Zero) return;

            RequireChannel().ExtendTail(this, amount, ripple);
        }

        //the change itself, once the channel has cleared the way
        internal void ApplyHeadExtend(Time amount)
        {
            Start -= amount;
            Duration += amount;
            OnHeadMoved(-amount);
        }

        internal void ApplyTailExtend(Time amount)
        {
            Duration += amount;
            OnTailMoved(amount);
        }

        /// <summary>Moves the start while the end and the content stay put, so the clip plays slower or faster.</summary>
        /// <remarks>Growing overwrites whatever the clip grows into; <see cref="Speed"/> takes up the change.</remarks>
        /// <param name="amount">Positive moves the start earlier (longer, slower); negative later (shorter, faster). It stops at the timeline's start and at <see cref="MinimumDuration"/>.</param>
        /// <exception cref="InvalidOperationException">The clip isn't placed.</exception>
        public void StretchStart(Time amount)
        {
            amount = ClampStretch(amount);
            // the head cannot go before the start of the timeline
            if (amount > Start) amount = Start;
            if (amount == Time.Zero) return;

            RequireChannel().StretchHead(this, amount);
        }

        /// <summary>Moves the end while the start and the content stay put, so the clip plays slower or faster.</summary>
        /// <remarks>Growing overwrites whatever the clip grows into; <see cref="Speed"/> takes up the change.</remarks>
        /// <param name="amount">Positive moves the end later (longer, slower); negative earlier (shorter, faster). It stops at <see cref="MinimumDuration"/>.</param>
        /// <exception cref="InvalidOperationException">The clip isn't placed.</exception>
        public void StretchEnd(Time amount)
        {
            amount = ClampStretch(amount);
            if (amount == Time.Zero) return;

            RequireChannel().StretchTail(this, amount);
        }

        // a stretch may shrink the clip, but never below the minimum duration
        private Time ClampStretch(Time amount)
        {
            Time maxShrink = Duration - MinimumDuration;
            if (maxShrink < Time.Zero) maxShrink = Time.Zero;

            return amount < -maxShrink ? -maxShrink : amount;
        }

        //the change itself, once the channel has cleared the way; the content range is held and Speed takes up the new
        //Duration, keeping its direction. A frozen clip just gets longer or shorter, its keyframes staying on the timeline
        internal void ApplyStretch(Time newStart, Time newDuration)
        {
            Time content = ContentDuration;
            Time headMove = newStart - Start;

            Start = newStart;
            Duration = newDuration;

            if (Frozen) ShiftEffectKeyframes(-headMove);
            else if (newDuration > Time.Zero && content > Time.Zero) SetSpeed(new Rational(content.Ticks, newDuration.Ticks) * _speed.Sign);
        }

        //loading: the timing exactly as saved, without the checks and trims setting Speed does
        internal void RestoreTiming(Rational speed, Rational speedBeforeFreeze, Time freezeAt)
        {
            _speed = speed;
            _speedBeforeFreeze = speedBeforeFreeze;
            _freezeAt = freezeAt;
        }

        /// <summary>Copies the speed, freeze and reverse settings onto a copy of this clip.</summary>
        /// <typeparam name="T">The kind of clip.</typeparam>
        /// <param name="copy">The copy.</param>
        /// <returns>The copy.</returns>
        protected T CopyTimingTo<T>(T copy) where T : Clip
        {
            copy._speed = _speed;
            copy._speedBeforeFreeze = _speedBeforeFreeze;
            copy._freezeAt = _freezeAt;
            return copy;
        }

        /// <inheritdoc/>
        public void Move(Time newStart, Channel? targetChannel = null) =>
            RequireChannel().Move(this, newStart, targetChannel, ripple: false);

        /// <inheritdoc/>
        public void RippleMove(Time newStart, Channel? targetChannel = null) =>
            RequireChannel().Move(this, newStart, targetChannel, ripple: true);

        /// <inheritdoc/>
        public void Split(Time at) => RequireChannel().SplitClip(this, at);

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
        public bool StartsAt(Time time) => Start == time;
        /// <summary>Whether the clip ends at a time.</summary>
        /// <param name="time">The timeline time.</param>
        /// <returns>True when <see cref="End"/> is <paramref name="time"/>.</returns>
        public bool EndsAt(Time time) => End == time;

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
        public Time IntersectionWith(Clip other)
        {
            Time start = Start > other.Start ? Start : other.Start;
            Time end = End < other.End ? End : other.End;
            return end > start ? end - start : Time.Zero;
        }

        /// <summary>The gap between the clip and another.</summary>
        /// <param name="other">The other clip.</param>
        /// <returns>The time between the end of the earlier clip and the start of the later; zero when they overlap.</returns>
        public Time DistanceFrom(Clip other)
        {
            if (IntersectionWith(other) > Time.Zero) return Time.Zero;

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
