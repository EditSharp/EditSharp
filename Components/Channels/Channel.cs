using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components.Clips;
using EditSharp.Components.Transitions;
using EditSharp.History;
using EditSharp.Editing;
 
namespace EditSharp.Components.Channels
{
    /// <summary>
    /// A single row of clips. Enforces the one invariant that matters here:
    /// clips on a channel never overlap, except exactly two joined by a
    /// Transition may overlap for that transition's Duration.
    ///
    /// REWRITE note ("clips are graphs"): the old TimelineVideoClip/
    /// TimelineAudioClip Clip subtypes are gone — an embedded nested
    /// Timeline is now just a TimelineVideoInputNode/TimelineAudioInputNode
    /// somewhere inside an ordinary VideoClip's/AudioClip's graph (see
    /// Clip.cs's own remarks). That means the cycle-check and Timeline.UsedBy
    /// bookkeeping that used to live in Timeline.AddTimelineClipCore (a
    /// SEPARATE add path from ordinary Channel.AddClip, reserved
    /// specifically for the old dedicated clip types) has moved here, into
    /// AddClipCore — the ONE place any clip newly enters a channel via the
    /// public Add path — generalized to scan whatever embed nodes a clip's
    /// graph happens to contain, of which there can now be any number
    /// (zero, one, or several, in a graph with multiple InputNodes). See
    /// Timeline.ValidateNoCycle/RegisterEmbeddedTimelines/NotifyClipDetached.
    /// </summary>
    public abstract class Channel
    {
        /// <summary>Runtime identity (not saved), e.g. for tapping a channel's audio.</summary>
        public Guid Id { get; } = Guid.NewGuid();

        string _name = "Channel";
        [Editable("Name")]
        public string Name { get => _name; set => Transaction.Set(this, ref _name, value, static (o, v) => o._name = v); }
 
        private readonly SortedDictionary<TimeSpan, Clip> _clips = new();
        private readonly List<Transition> _transitions = [];
 
        public IReadOnlyCollection<Clip> Clips => _clips.Values;
        public IReadOnlyList<Transition> Transitions => _transitions;
 
        Timeline? _timeline;
        public Timeline? Timeline { get => _timeline; internal set => Transaction.Set(this, ref _timeline, value, static (o, v) => o._timeline = v); }
 
        /// <summary>
        /// This channel's position among channels of its own kind — 0 is
        /// the bottom layer, matching VideoChannels'/AudioChannels' own
        /// index order (see Timeline's remarks on the video/audio split).
        /// -1 if this channel isn't currently placed on any Timeline.
        /// </summary>
        public int Index => Timeline?.IndexOf(this) ?? -1;

        /// <summary>
        /// Swaps this channel with the one directly above it (higher index
        /// — for VideoChannels that's the next layer drawn on top;
        /// AudioChannels' order carries no acoustic meaning, only UI
        /// listing order — see AudioMixer). No-op if this channel is
        /// already the topmost of its kind, or isn't placed on a Timeline.
        /// </summary>
        public void MoveUp() => Timeline?.SwapChannel(this, +1);

        /// <summary>Swaps this channel with the one directly below it. No-op at the bottom.</summary>
        public void MoveDown() => Timeline?.SwapChannel(this, -1);
 
        public TimeSpan End => _clips.Count == 0 ? TimeSpan.Zero : _clips.Values.Max(c => c.End);
 
        protected internal abstract bool IsValidClipType(Clip clip);
 
        private void ValidateType(Clip clip)
        {
            if (!IsValidClipType(clip))
                throw new ArgumentException(
                    $"{clip.GetType().Name} is not a valid clip type for {GetType().Name}.", nameof(clip));
        }
 
        // ---------------------------------------------------------------
        // Add
        // ---------------------------------------------------------------
 
        public Clip AddClip(Clip clip) => AddClipCore(clip, ripple: false);
        public Clip RippleAddClip(Clip clip) => AddClipCore(clip, ripple: true);
 
        private Clip AddClipCore(Clip clip, bool ripple)
        {
            ValidateType(clip);
 
            //cycle-check BEFORE any mutation — a clip whose graph embeds a
            //nested Timeline that would create a cycle must be rejected
            //outright, not partially placed and then rolled back
            Timeline?.ValidateNoCycle(clip);
 
            if (ripple) RippleFrom(clip.Start, clip.Duration);
            else Overwrite(clip.Start, clip.End);
 
            PlaceInternal(clip);
 
            //now that placement succeeded, register this clip's embedded
            //timelines (if any) in their own Timeline.UsedBy
            Timeline?.RegisterEmbeddedTimelines(clip);
 
            return clip;
        }
 
        /// <summary>
        /// A placed clip's Start is its key here, so anything that moves a
        /// clip's Start in place — a head trim — has to move the entry with
        /// it. Left stale, the clip can no longer be removed by its Start,
        /// so a later Move/Overwrite scan finds the ghost entry and trims
        /// the clip against itself.
        /// </summary>
        internal void Rekey(Clip clip, TimeSpan previousStart)
        {
            TimeSpan key = clip.Start;
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

        //the two halves of the dictionary's bookkeeping, each recorded so an
        //undo puts the entry back under the key it had - see Transaction
        private void Place(Clip clip)
        {
            TimeSpan key = clip.Start;
            Transaction.Apply(() => _clips[key] = clip, () => _clips.Remove(key), "place clip");
        }

        private void Unplace(Clip clip)
        {
            TimeSpan key = clip.Start;
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
 
        // ---------------------------------------------------------------
        // Move
        // ---------------------------------------------------------------
 
        /// <summary>
        /// KNOWN GAP, carried forward unchanged from before this rewrite:
        /// a cross-Timeline move of a clip embedding a nested Timeline
        /// doesn't re-run the cycle check or update Timeline.UsedBy — Move
        /// goes through PlaceInternal directly, not AddClipCore, exactly
        /// like every other internal repositioning helper here (Extend/
        /// Split) deliberately does not re-trigger that bookkeeping either.
        /// </summary>
        internal void Move(Clip clip, TimeSpan newStart, Channel? targetChannel, bool ripple)
        {
            clip.Channel?.DetachClip(clip);
 
            Channel destination = targetChannel ?? this;
 
            if (ripple) destination.RippleFrom(newStart, clip.Duration);
            else destination.Overwrite(newStart, newStart + clip.Duration);
 
            clip.Start = newStart;
            destination.PlaceInternal(clip);
        }
 
        // ---------------------------------------------------------------
        // Extend
        // ---------------------------------------------------------------
 
        /// <summary>
        /// `exclude`, when given, is skipped entirely by the Overwrite scan
        /// this performs — used ONLY by AddTransition below, where `clip`
        /// and `exclude` are the two clips a transition is legitimately
        /// making overlap. Every other caller (the public Clip.ExtendStart
        /// path, used for an ordinary extend with no transition involved)
        /// passes null here and gets the normal "overwrite whatever's in
        /// the way" behavior against every neighbor, adjacent or not.
        /// </summary>
        internal void ExtendHead(Clip clip, TimeSpan amount, bool ripple, Clip? exclude = null)
        {
            Unplace(clip);
 
            TimeSpan newStart = clip.Start - amount;
            if (ripple) RippleFrom(newStart, amount);
            else Overwrite(newStart, clip.Start, exclude);
 
            clip.ApplyHeadExtend(amount);
            PlaceInternal(clip);
            ReconcileTransitionsFor(clip);
        }
 
        internal void ExtendTail(Clip clip, TimeSpan amount, bool ripple, Clip? exclude = null)
        {
            Unplace(clip);
 
            if (ripple) RippleFrom(clip.End, amount);
            else Overwrite(clip.End, clip.End + amount, exclude);
 
            clip.ApplyTailExtend(amount);
            PlaceInternal(clip);
            ReconcileTransitionsFor(clip);
        }

        // ---------------------------------------------------------------
        // Stretch — see Clip.StretchStart/StretchEnd. Only a growing
        // stretch can overlap a neighbor; a shrinking one never touches
        // anything but the clip itself. Overwrite semantics only, like a
        // plain (non-ripple) Extend.
        // ---------------------------------------------------------------

        internal void StretchHead(Clip clip, TimeSpan amount)
        {
            Unplace(clip);

            TimeSpan newStart = clip.Start - amount;
            if (amount > TimeSpan.Zero) Overwrite(newStart, clip.Start);

            clip.ApplyStretch(newStart, clip.Duration + amount);
            PlaceInternal(clip);
            ReconcileTransitionsFor(clip);
        }

        internal void StretchTail(Clip clip, TimeSpan amount)
        {
            Unplace(clip);

            if (amount > TimeSpan.Zero) Overwrite(clip.End, clip.End + amount);

            clip.ApplyStretch(clip.Start, clip.Duration + amount);
            PlaceInternal(clip);
            ReconcileTransitionsFor(clip);
        }
 
        // ---------------------------------------------------------------
        // Split / Delete
        // ---------------------------------------------------------------
 
        internal void SplitClip(Clip clip, TimeSpan at)
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

        /// <summary>
        /// Takes everything inside [start, end) off this channel: a clip
        /// wholly inside goes, one spanning an edge is trimmed to the edge,
        /// one spanning both is split around the range. Overwrite
        /// semantics with nothing placed in the hole.
        /// </summary>
        public void RemoveRange(TimeSpan start, TimeSpan end)
        {
            if (end <= start) return;

            //the clips that will be taken off outright, for the link/embed
            //bookkeeping a removal owes them - Overwrite alone does not
            List<Clip> removed = _clips.Values.Where(c => c.Start >= start && c.End <= end).ToList();

            Overwrite(start, end);

            foreach (Clip clip in removed) Timeline?.NotifyClipDetached(clip);
        }

        /// <summary>RemoveRange, then closes the gap: everything that started at or after `end` moves earlier by the range's length.</summary>
        public void RippleRemoveRange(TimeSpan start, TimeSpan end)
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
 
        // ---------------------------------------------------------------
        // Transitions
        // ---------------------------------------------------------------
 
        /// <summary>
        /// FOUND IN THE FIELD, FIXED: this used to call the ordinary
        /// `to.ExtendStart(achievableHalf)` / `from.ExtendEnd(achievableHalf)`
        /// — both of which resolve conflicts via the normal Overwrite path,
        /// with NO exclusion. Since `to.Start == from.End` by construction
        /// (that's what "adjacent" means here), each extend's own Overwrite
        /// call found the OTHER transition partner sitting exactly in the
        /// region it was trying to claim, and trimmed it right back —
        /// extending `to`'s head trimmed `from`'s tail back to where it
        /// started, then extending `from`'s tail pushed `to`'s head back
        /// out to where IT started. Net effect: both clips ended up
        /// completely unchanged, `transition.Duration` was set to a value
        /// that no longer matched `to.Start == from.End` at all, and the
        /// very next trim/extend on either clip would silently delete this
        /// "transition" via ReconcileTransitionsFor's own consistency
        /// check — no crossfade region was ever actually created.
        ///
        /// Fix: call ExtendHead/ExtendTail directly (bypassing Clip's own
        /// ExtendStart/ExtendEnd wrappers) with each other passed as
        /// `exclude`, since a transition's whole point is to make exactly
        /// these two clips overlap — every OTHER neighbor on the channel
        /// still gets the normal overwrite treatment.
        /// </summary>
        public Transition AddTransition(Transition transition)
        {
            Clip from = transition.From;
            Clip to = transition.To;
 
            if (from.Channel != this || to.Channel != this)
                throw new ArgumentException("Both clips must already be placed on this channel.");
            if (to.Start != from.End)
                throw new ArgumentException("Transition.From and Transition.To must be adjacent (To.Start == From.End).");
 
            TimeSpan requestedHalf = TimeSpan.FromTicks(transition.Duration.Ticks / 2);
 
            //each clip grows into the other by half, as far as its content allows
            TimeSpan achievableHalf = requestedHalf;
            if (to.HeadExtendLimit < achievableHalf) achievableHalf = to.HeadExtendLimit;
            if (from.TailExtendLimit < achievableHalf) achievableHalf = from.TailExtendLimit;
 
            if (achievableHalf > TimeSpan.Zero)
            {
                ExtendHead(to, achievableHalf, ripple: false, exclude: from);
                ExtendTail(from, achievableHalf, ripple: false, exclude: to);
            }
 
            transition.Duration = achievableHalf + achievableHalf;
            Transaction.Apply(() => _transitions.Add(transition), () => _transitions.Remove(transition), "add transition");
 
            return transition;
        }
 
        public void RemoveTransition(Transition transition) => RemoveTransitionsWhere(t => ReferenceEquals(t, transition));
 
        internal void ReconcileTransitionsFor(Clip clip)
        {
            RemoveTransitionsWhere(t => (t.From == clip || t.To == clip) && !IsTransitionValid(t));
        }
 
        private static bool IsTransitionValid(Transition t) =>
            t.From.Duration > TimeSpan.Zero && t.To.Duration > TimeSpan.Zero && t.To.Start == t.From.End - t.Duration;
 
        // ---------------------------------------------------------------
        // Overwrite / Ripple mechanics
        // ---------------------------------------------------------------
 
        /// <summary>
        /// `exclude`, when given, is never trimmed/split/deleted by this
        /// scan even if its span falls inside [newStart, newEnd) — see
        /// AddTransition, the only caller that ever passes one.
        /// </summary>
        private void Overwrite(TimeSpan newStart, TimeSpan newEnd, Clip? exclude = null)
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
                    //target spans wider than the incoming region on both sides — carve a hole
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
 
        /// <summary>The reverse of RippleFrom: every clip starting at or after `at` moves EARLIER by `amount`. Earliest first, so each clip moves into room the one before it has just left.</summary>
        private void RippleClose(TimeSpan at, TimeSpan amount)
        {
            if (amount <= TimeSpan.Zero) return;

            foreach (Clip clip in _clips.Values.Where(c => c.Start >= at).OrderBy(c => c.Start).ToList())
            {
                Unplace(clip);
                clip.Start -= amount;
                Place(clip);
            }
        }

        private void RippleFrom(TimeSpan at, TimeSpan amount)
        {
            if (amount <= TimeSpan.Zero) return;
 
            foreach (Clip clip in _clips.Values.Where(c => c.Start >= at).OrderByDescending(c => c.Start).ToList())
            {
                Unplace(clip);
                clip.Start += amount;
                Place(clip);
            }
        }
 
        /// <summary>
        /// Splits `target` into [target.Start, cutStart) and [cutEnd, target.End),
        /// preserving LinkGroupId. Uses each fragment's OWN TrimEnd/TrimStart
        /// rather than raw Start/Duration field assignment specifically so
        /// OnHeadInPointShift runs on the tail fragment — that's what
        /// correctly advances every trimmable input node's own in-point
        /// (VideoSourceNode.Source.Start, TimelineVideoInputNode.Reference.Start,
        /// etc.) to match where the tail fragment now actually starts
        /// reading from. Raw field assignment would leave a split tail
        /// silently pointing at the wrong in-point.
        /// </summary>
        private static (Clip Head, Clip Tail) SplitFragments(Clip target, TimeSpan cutStart, TimeSpan cutEnd)
        {
            //the fragments are new objects being shaped, not edits to the
            //project - placing them is what gets recorded
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
 