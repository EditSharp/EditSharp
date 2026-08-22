using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components.Clips;
using EditSharp.Components.Transitions;
 
namespace EditSharp.Components
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
        public string Name { get; set; } = "Channel";
 
        private readonly SortedDictionary<TimeSpan, Clip> _clips = new();
        private readonly List<Transition> _transitions = [];
 
        public IReadOnlyCollection<Clip> Clips => _clips.Values;
        public IReadOnlyList<Transition> Transitions => _transitions;
 
        public Timeline? Timeline { get; internal set; }
 
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
 
        private void PlaceInternal(Clip clip)
        {
            _clips[clip.Start] = clip;
            clip.Channel = this;
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
 
        internal void ExtendHead(Clip clip, TimeSpan amount, bool ripple)
        {
            _clips.Remove(clip.Start);
 
            TimeSpan newStart = clip.Start - amount;
            if (ripple) RippleFrom(newStart, amount);
            else Overwrite(newStart, clip.Start);
 
            clip.ApplyHeadExtend(amount);
            PlaceInternal(clip);
            ReconcileTransitionsFor(clip);
        }
 
        internal void ExtendTail(Clip clip, TimeSpan amount, bool ripple)
        {
            _clips.Remove(clip.Start);
 
            if (ripple) RippleFrom(clip.End, amount);
            else Overwrite(clip.End, clip.End + amount);
 
            clip.ApplyTailExtend(amount);
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
 
        private void DetachClip(Clip clip)
        {
            _clips.Remove(clip.Start);
            _transitions.RemoveAll(t => t.From == clip || t.To == clip);
            clip.Channel = null;
        }
 
        // ---------------------------------------------------------------
        // Transitions
        // ---------------------------------------------------------------
 
        public Transition AddTransition(Transition transition)
        {
            Clip from = transition.From;
            Clip to = transition.To;
 
            if (from.Channel != this || to.Channel != this)
                throw new ArgumentException("Both clips must already be placed on this channel.");
            if (to.Start != from.End)
                throw new ArgumentException("Transition.From and Transition.To must be adjacent (To.Start == From.End).");
 
            TimeSpan requestedHalf = TimeSpan.FromTicks(transition.Duration.Ticks / 2);
 
            TimeSpan toCeiling = to.MaxHeadExtend();
            TimeSpan achievableHalf = toCeiling == TimeSpan.MaxValue || requestedHalf <= toCeiling
                ? requestedHalf : toCeiling;
 
            to.ExtendStart(achievableHalf);
            from.ExtendEnd(achievableHalf);
 
            transition.Duration = achievableHalf + achievableHalf;
            _transitions.Add(transition);
 
            return transition;
        }
 
        public void RemoveTransition(Transition transition) => _transitions.Remove(transition);
 
        internal void ReconcileTransitionsFor(Clip clip)
        {
            _transitions.RemoveAll(t => (t.From == clip || t.To == clip) && !IsTransitionValid(t));
        }
 
        private static bool IsTransitionValid(Transition t) =>
            t.From.Duration > TimeSpan.Zero && t.To.Duration > TimeSpan.Zero && t.To.Start == t.From.End - t.Duration;
 
        // ---------------------------------------------------------------
        // Overwrite / Ripple mechanics
        // ---------------------------------------------------------------
 
        private void Overwrite(TimeSpan newStart, TimeSpan newEnd)
        {
            foreach (Clip target in _clips.Values.ToList())
            {
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
 
        private void RippleFrom(TimeSpan at, TimeSpan amount)
        {
            if (amount <= TimeSpan.Zero) return;
 
            foreach (Clip clip in _clips.Values.Where(c => c.Start >= at).OrderByDescending(c => c.Start).ToList())
            {
                _clips.Remove(clip.Start);
                clip.Start += amount;
                _clips[clip.Start] = clip;
            }
        }
 
        /// <summary>
        /// Splits `target` into [target.Start, cutStart) and [cutEnd, target.End),
        /// preserving LinkGroupId. Uses each fragment's OWN TrimEnd/TrimStart
        /// rather than raw Start/Duration field assignment specifically so
        /// OnHeadInPointShift runs on the tail fragment — that's what
        /// correctly advances every trimmable input node's own in-point
        /// (MediaSourceNode.Source.Start, TimelineVideoInputNode.Reference.Start,
        /// etc.) to match where the tail fragment now actually starts
        /// reading from. Raw field assignment would leave a split tail
        /// silently pointing at the wrong in-point.
        /// </summary>
        private static (Clip Head, Clip Tail) SplitFragments(Clip target, TimeSpan cutStart, TimeSpan cutEnd)
        {
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
 