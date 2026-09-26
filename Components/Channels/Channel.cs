using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components.Clips;
using EditSharp.Components.Transitions;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Channels
{
    /// <summary>One row of clips on a timeline.</summary>
    /// <remarks>Clips on a channel never overlap, except two joined by a transition, which overlap for its duration. Adding or moving a clip onto occupied time overwrites what's there (trimming, splitting or removing it); the ripple variants push later clips along instead.</remarks>
    public abstract class Channel
    {
        /// <summary>Identifies the channel while the program runs, such as for tapping its audio; it isn't saved.</summary>
        public Guid Id { get; } = Guid.NewGuid();

        string _name = "Channel";
        /// <summary>The channel's name, as editors show it.</summary>
        [Editable("Name")]
        public string Name { get => _name; set => Transaction.Set(this, ref _name, value, static (o, v) => o._name = v); }

        private readonly SortedDictionary<Time, Clip> _clips = new();
        private readonly List<Transition> _transitions = [];

        /// <summary>The clips on the channel, in order of start.</summary>
        public IReadOnlyCollection<Clip> Clips => _clips.Values;

        /// <summary>The transitions between clips on the channel.</summary>
        public IReadOnlyList<Transition> Transitions => _transitions;

        Timeline? _timeline;
        /// <summary>The timeline the channel is on; null when it isn't on one.</summary>
        public Timeline? Timeline { get => _timeline; internal set => Transaction.Set(this, ref _timeline, value, static (o, v) => o._timeline = v); }

        /// <summary>The channel's position among the timeline's channels of its kind, from 0 at the bottom; -1 when it isn't on a timeline.</summary>
        public int Index => Timeline?.IndexOf(this) ?? -1;

        /// <summary>Swaps the channel with the one above it; does nothing at the top or off a timeline.</summary>
        /// <remarks>A video channel above another draws over it. Audio channels' order only affects how they're listed.</remarks>
        public void MoveUp() => Timeline?.SwapChannel(this, +1);

        /// <summary>Swaps the channel with the one below it; does nothing at the bottom or off a timeline.</summary>
        public void MoveDown() => Timeline?.SwapChannel(this, -1);

        /// <summary>Where the channel's last clip ends; zero when it has none.</summary>
        public Time End => _clips.Count == 0 ? Time.Zero : _clips.Values.Max(c => c.End);

        /// <summary>Whether a clip is the kind this channel holds.</summary>
        /// <param name="clip">The clip.</param>
        /// <returns>True when it can go on this channel.</returns>
        protected internal abstract bool IsValidClipType(Clip clip);

        private void ValidateType(Clip clip)
        {
            if (!IsValidClipType(clip))
                throw new ArgumentException(
                    $"{clip.GetType().Name} is not a valid clip type for {GetType().Name}.", nameof(clip));
        }

        // ---- add ----

        /// <summary>Places a clip at its <see cref="Clip.Start"/>, overwriting whatever is there.</summary>
        /// <param name="clip">The clip; it mustn't be on a channel already.</param>
        /// <returns><paramref name="clip"/>.</returns>
        /// <exception cref="ArgumentException">The clip is the wrong kind for this channel.</exception>
        /// <exception cref="InvalidOperationException">The clip embeds a timeline that contains this one.</exception>
        public Clip AddClip(Clip clip) => AddClipCore(clip, ripple: false);
        /// <summary>Places a clip at its <see cref="Clip.Start"/>, moving every clip from there on later by its length.</summary>
        /// <param name="clip">The clip; it mustn't be on a channel already.</param>
        /// <returns><paramref name="clip"/>.</returns>
        /// <exception cref="ArgumentException">The clip is the wrong kind for this channel.</exception>
        /// <exception cref="InvalidOperationException">The clip embeds a timeline that contains this one.</exception>
        public Clip RippleAddClip(Clip clip) => AddClipCore(clip, ripple: true);

        private Clip AddClipCore(Clip clip, bool ripple)
        {
            ValidateType(clip);

            //before anything changes, so a refused clip isn't half placed
            Timeline?.ValidateNoCycle(clip);

            if (ripple) RippleFrom(clip.Start, clip.Duration);
            else Overwrite(clip.Start, clip.End);

            PlaceInternal(clip);

            Timeline?.RegisterEmbeddedTimelines(clip);

            return clip;
        }

        //clips are keyed by Start, so a head trim, which moves Start in place, moves the entry with it
        internal void Rekey(Clip clip, Time previousStart)
        {
            Time key = clip.Start;
            bool hadOld = _clips.TryGetValue(previousStart, out Clip? placed) && ReferenceEquals(placed, clip);

            Transaction.Apply(
                () => { if (hadOld) _clips.Remove(previousStart); _clips[key] = clip; },
                () => { _clips.Remove(key); if (hadOld) _clips[previousStart] = clip; },
                "rekey clip");
        }

        private void PlaceInternal(Clip clip)
        {
            Place(clip);
            clip.Channel = this;
        }

        //each recorded, so an undo puts the entry back under the key it had
        private void Place(Clip clip)
        {
            Time key = clip.Start;
            Transaction.Apply(() => _clips[key] = clip, () => _clips.Remove(key), "place clip");
        }

        private void Unplace(Clip clip)
        {
            Time key = clip.Start;
            Transaction.Apply(() => _clips.Remove(key), () => _clips[key] = clip, "unplace clip");
        }

        private void RemoveTransitionsWhere(Predicate<Transition> match)
        {
            List<(int index, Transition transition)> removed = [];
            for (int i = 0; i < _transitions.Count; i++)
                if (match(_transitions[i])) removed.Add((i, _transitions[i]));

            if (removed.Count == 0) return;

            Transaction.Apply(
                () => { foreach ((_, Transition t) in removed) _transitions.Remove(t); },
                () => { foreach ((int index, Transition t) in removed) _transitions.Insert(Math.Min(index, _transitions.Count), t); },
                "remove transitions");
        }

        // ---- move ----

        internal void Move(Clip clip, Time newStart, Channel? targetChannel, bool ripple)
        {
            Channel destination = targetChannel ?? this;
            destination.ValidateType(clip);

            //a clip moving to another timeline takes its embeds with it; check for a cycle before anything moves
            Timeline? from = Timeline, to = destination.Timeline;
            bool crossing = !ReferenceEquals(from, to);
            if (crossing) to?.ValidateNoCycle(clip);

            clip.Channel?.DetachClip(clip);

            if (ripple) destination.RippleFrom(newStart, clip.Duration);
            else destination.Overwrite(newStart, newStart + clip.Duration);

            clip.Start = newStart;
            destination.PlaceInternal(clip);

            if (crossing)
            {
                from?.UnregisterEmbeddedTimelines(clip);
                to?.RegisterEmbeddedTimelines(clip);
            }
        }

        // ---- extend ----

        //`exclude` is left alone by the overwrite: AddTransition's two clips are meant to overlap
        internal void ExtendHead(Clip clip, Time amount, bool ripple, Clip? exclude = null)
        {
            Unplace(clip);

            Time newStart = clip.Start - amount;
            if (ripple) RippleFrom(newStart, amount);
            else Overwrite(newStart, clip.Start, exclude);

            clip.ApplyHeadExtend(amount);
            PlaceInternal(clip);
            ReconcileTransitionsFor(clip);
        }

        internal void ExtendTail(Clip clip, Time amount, bool ripple, Clip? exclude = null)
        {
            Unplace(clip);

            if (ripple) RippleFrom(clip.End, amount);
            else Overwrite(clip.End, clip.End + amount, exclude);

            clip.ApplyTailExtend(amount);
            PlaceInternal(clip);
            ReconcileTransitionsFor(clip);
        }

        internal void StretchHead(Clip clip, Time amount)
        {
            Unplace(clip);

            Time newStart = clip.Start - amount;
            if (amount > Time.Zero) Overwrite(newStart, clip.Start);

            clip.ApplyStretch(newStart, clip.Duration + amount);
            PlaceInternal(clip);
            ReconcileTransitionsFor(clip);
        }

        internal void StretchTail(Clip clip, Time amount)
        {
            Unplace(clip);

            if (amount > Time.Zero) Overwrite(clip.End, clip.End + amount);

            clip.ApplyStretch(clip.Start, clip.Duration + amount);
            PlaceInternal(clip);
            ReconcileTransitionsFor(clip);
        }

        // ---- split and delete ----

        internal void SplitClip(Clip clip, Time at)
        {
            if (at <= clip.Start || at >= clip.End)
                throw new ArgumentOutOfRangeException(nameof(at), "Split point must be strictly inside the clip.");

            (Clip head, Clip tail) = SplitFragments(clip, at, at);

            DetachClip(clip);
            PlaceInternal(head);
            PlaceInternal(tail);
        }

        internal void RemoveClip(Clip clip)
        {
            if (clip.Channel != this) return;

            DetachClip(clip);
            Timeline?.NotifyClipDetached(clip);
        }

        /// <summary>Clears a range of the channel, leaving a gap.</summary>
        /// <remarks>A clip wholly inside goes, one across an edge is trimmed to it, and one across both is split around the range.</remarks>
        /// <param name="start">Where the range starts.</param>
        /// <param name="end">Where it ends; an empty or reversed range does nothing.</param>
        public void RemoveRange(Time start, Time end)
        {
            if (end <= start) return;

            //the clips taken off outright, which are owed the link and embed bookkeeping of a removal
            List<Clip> removed = _clips.Values.Where(c => c.Start >= start && c.End <= end).ToList();

            Overwrite(start, end);

            foreach (Clip clip in removed) Timeline?.NotifyClipDetached(clip);
        }

        /// <summary>Clears a range of the channel and closes the gap: everything after it moves earlier by its length.</summary>
        /// <param name="start">Where the range starts.</param>
        /// <param name="end">Where it ends; an empty or reversed range does nothing.</param>
        public void RippleRemoveRange(Time start, Time end)
        {
            if (end <= start) return;

            RemoveRange(start, end);
            RippleClose(end, end - start);
        }

        private void DetachClip(Clip clip)
        {
            Unplace(clip);
            RemoveTransitionsWhere(t => t.From == clip || t.To == clip);
            clip.Channel = null;
        }

        // ---- transitions ----

        /// <summary>Joins two adjacent clips with a transition.</summary>
        /// <remarks>Each clip grows into the other by half the transition's <see cref="Transition.Duration"/>, as far as its sources have material; the duration is then set to what was achieved, which can be shorter, or zero.</remarks>
        /// <param name="from">The clip the transition leaves.</param>
        /// <param name="to">The clip it arrives at, starting where <paramref name="from"/> ends.</param>
        /// <param name="transition">The transition, with its duration set.</param>
        /// <returns><paramref name="transition"/>.</returns>
        /// <exception cref="ArgumentException">Either clip isn't on this channel, or they aren't adjacent.</exception>
        public Transition AddTransition(Clip from, Clip to, Transition transition)
        {
            if (from.Channel != this || to.Channel != this)
                throw new ArgumentException("Both clips must already be placed on this channel.");
            if (to.Start != from.End)
                throw new ArgumentException("The clips must be adjacent: `to` starts where `from` ends.");

            transition.From = from;
            transition.To = to;

            Time requestedHalf = transition.Duration / 2;

            //each clip grows into the other by half, as far as its content allows; they're excluded from
            //each other's overwrite, since overlapping is the point
            Time achievableHalf = requestedHalf;
            if (to.HeadExtendLimit < achievableHalf) achievableHalf = to.HeadExtendLimit;
            if (from.TailExtendLimit < achievableHalf) achievableHalf = from.TailExtendLimit;

            if (achievableHalf > Time.Zero)
            {
                ExtendHead(to, achievableHalf, ripple: false, exclude: from);
                ExtendTail(from, achievableHalf, ripple: false, exclude: to);
            }

            transition.Duration = achievableHalf + achievableHalf;
            Transaction.Apply(() => _transitions.Add(transition), () => _transitions.Remove(transition), "add transition");

            return transition;
        }

        /// <summary>Removes a transition; the clips keep the lengths it gave them.</summary>
        /// <param name="transition">The transition.</param>
        public void RemoveTransition(Transition transition) => RemoveTransitionsWhere(t => ReferenceEquals(t, transition));

        internal void ReconcileTransitionsFor(Clip clip)
        {
            RemoveTransitionsWhere(t => (t.From == clip || t.To == clip) && !IsTransitionValid(t));
        }

        private static bool IsTransitionValid(Transition t) =>
            t.From.Duration > Time.Zero && t.To.Duration > Time.Zero && t.To.Start == t.From.End - t.Duration;

        // ---- overwrite and ripple ----

        //trims, splits or removes whatever is in [newStart, newEnd), except `exclude`
        private void Overwrite(Time newStart, Time newEnd, Clip? exclude = null)
        {
            foreach (Clip target in _clips.Values.ToList())
            {
                if (ReferenceEquals(target, exclude)) continue;
                if (target.End <= newStart || target.Start >= newEnd) continue; //no overlap

                bool coveredHead = target.Start >= newStart;
                bool coveredTail = target.End <= newEnd;

                if (coveredHead && coveredTail)
                {
                    DetachClip(target);
                }
                else if (!coveredHead && !coveredTail)
                {
                    //wider than the region on both sides: carve a hole
                    (Clip before, Clip after) = SplitFragments(target, newStart, newEnd);
                    DetachClip(target);
                    PlaceInternal(before);
                    PlaceInternal(after);
                }
                else if (!coveredHead) // target.Start < newStart, target.End <= newEnd
                {
                    target.TrimEnd(target.End - newStart);
                }
                else // coveredHead && !coveredTail: target.Start >= newStart, target.End > newEnd
                {
                    target.TrimStart(newEnd - target.Start);
                }
            }
        }

        //every clip from `at` on moves earlier by `amount`, earliest first so each moves into room just left
        private void RippleClose(Time at, Time amount)
        {
            if (amount <= Time.Zero) return;

            foreach (Clip clip in _clips.Values.Where(c => c.Start >= at).OrderBy(c => c.Start).ToList())
            {
                Unplace(clip);
                clip.Start -= amount;
                Place(clip);
            }
        }

        private void RippleFrom(Time at, Time amount)
        {
            if (amount <= Time.Zero) return;

            foreach (Clip clip in _clips.Values.Where(c => c.Start >= at).OrderByDescending(c => c.Start).ToList())
            {
                Unplace(clip);
                clip.Start += amount;
                Place(clip);
            }
        }

        //[target.Start, cutStart) and [cutEnd, target.End), keeping the link group. The fragments are trimmed
        //through TrimStart/TrimEnd so the tail's in-points and keyframes move with its new start
        private static (Clip Head, Clip Tail) SplitFragments(Clip target, Time cutStart, Time cutEnd)
        {
            //shaping new objects isn't an edit; placing them is what gets recorded
            using var _ = Transaction.Suppress();

            Clip head = target.Duplicate();
            head.LinkGroupId = target.LinkGroupId;
            head.TrimEnd(head.End - cutStart);

            Clip tail = target.Duplicate();
            tail.LinkGroupId = target.LinkGroupId;
            tail.TrimStart(cutEnd - tail.Start);

            return (head, tail);
        }
    }
}
