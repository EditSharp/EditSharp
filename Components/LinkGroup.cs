using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components.Channels;
using EditSharp.Components.Clips;

namespace EditSharp.Components
{
    /// <summary>
    /// A real object owning coordination logic, not just a shared Guid — see
    /// the schema doc. Carries no state beyond Id and which Timeline to
    /// derive membership from; Members is always a live scan (matches the
    /// doc: "IReadOnlyList&lt;Clip&gt; Members; // derived from
    /// Clip.LinkGroupId == Id"), so Timeline.GetLinkGroup handing back a
    /// fresh instance on every call is correct, not wasteful.
    ///
    /// Membership is flat — a Clip belongs to at most one LinkGroup (a
    /// single nullable LinkGroupId, not a collection); merge-on-link is how
    /// two groups combine instead of nesting.
    /// </summary>
    public sealed class LinkGroup : ITimelineEditable
    {
        public Guid Id { get; }
        private readonly Timeline _timeline;

        internal LinkGroup(Guid id, Timeline timeline)
        {
            Id = id;
            _timeline = timeline;
        }

        public IReadOnlyList<Clip> Members => [.. _timeline.AllClips().Where(c => c.LinkGroupId == Id)];

        // ---------------------------------------------------------------
        // Move — "moves every member by the same delta." newStart is
        // interpreted as where the group's own earliest member should
        // land; every member shifts by the same resulting delta.
        // ---------------------------------------------------------------

        public void Move(TimeSpan newStart, Channel? targetChannel = null) => MoveCore(newStart, targetChannel, ripple: false);
        public void RippleMove(TimeSpan newStart, Channel? targetChannel = null) => MoveCore(newStart, targetChannel, ripple: true);

        private void MoveCore(TimeSpan newStart, Channel? targetChannel, bool ripple)
        {
            if (targetChannel != null)
                throw new NotSupportedException(
                    "Cross-channel LinkGroup Move isn't supported — current design keeps group Move to " +
                    "a same-channel Start shift per member (see the schema doc's open-questions list). " +
                    "To move a linked group to a different channel, use MoveChannel/RippleMoveChannel instead.");

            List<Clip> members = [.. Members];
            if (members.Count == 0) return;

            TimeSpan delta = newStart - members.Min(m => m.Start);

            //capture every member's target Start BEFORE moving any of them —
            //moving one must never change the delta computed for another
            List<(Clip Clip, TimeSpan NewStart)> targets = [.. members.Select(m => (m, m.Start + delta))];

            foreach ((Clip clip, TimeSpan target) in targets)
            {
                if (ripple) clip.RippleMove(target);
                else clip.Move(target);
            }
        }

        // ---------------------------------------------------------------
        // MoveChannel — the answer to "move this linked group up/down a
        // channel." A single target Channel (like same-channel Move takes a
        // single target Start) doesn't make sense here: a linked group's
        // members live on channels of DIFFERENT kinds (a VideoClip and its
        // linked AudioClip can never share one channel), so there is no one
        // Channel object that's the right destination for every member at
        // once. A channel COUNT to shift by is the one version of "move a
        // channel" that means the same thing for every member simultaneously
        // — the video member moves `delta` VideoChannels, the audio member
        // moves the SAME `delta` AudioChannels, each independently within
        // its own kind's channel order. See Timeline.ResolveChannelDelta,
        // which does the actual per-kind index lookup (and creates new
        // channels of the matching kind on the way up, past the current top,
        // exactly as if you'd dragged the clip above the last track).
        //
        // Every member's own Start is left untouched — this only ever
        // changes WHICH channel a member lives on, never WHEN it plays.
        // ---------------------------------------------------------------

        public void MoveChannel(int delta) => MoveChannelCore(delta, ripple: false);
        public void RippleMoveChannel(int delta) => MoveChannelCore(delta, ripple: true);

        private void MoveChannelCore(int delta, bool ripple)
        {
            if (delta == 0) return;

            List<Clip> members = [.. Members];
            if (members.Count == 0) return;

            //resolve (and, if needed, create) every member's target channel
            //BEFORE moving any of them — same "compute every target first,
            //then apply" discipline MoveCore's own delta capture uses above,
            //so relocating one member can never perturb another member's own
            //target channel
            List<(Clip Clip, Channel Target)> targets = [];

            foreach (Clip clip in members)
            {
                Channel current = clip.Channel
                    ?? throw new InvalidOperationException("A LinkGroup member is not currently placed on any Channel.");

                targets.Add((clip, _timeline.ResolveChannelDelta(current, delta)));
            }

            foreach ((Clip clip, Channel target) in targets)
            {
                if (ripple) clip.RippleMove(clip.Start, target);
                else clip.Move(clip.Start, target);
            }
        }

        // ---------------------------------------------------------------
        // Trim/Extend — delta applied uniformly to every member's
        // corresponding edge, clamped to whatever the most-constrained
        // member allows (see the schema doc).
        // ---------------------------------------------------------------

        public void TrimStart(TimeSpan amount) => TrimCore(amount, atStart: true);
        public void TrimEnd(TimeSpan amount) => TrimCore(amount, atStart: false);

        private void TrimCore(TimeSpan amount, bool atStart)
        {
            if (amount <= TimeSpan.Zero) return;

            List<Clip> members = [.. Members];
            if (members.Count == 0) return;

            TimeSpan achievable = members
                .Select(m => m.Duration - Clip.MinimumDuration)
                .Where(max => max >= TimeSpan.Zero)
                .DefaultIfEmpty(TimeSpan.Zero)
                .Min();

            TimeSpan clamped = amount < achievable ? amount : achievable;
            if (clamped <= TimeSpan.Zero) return;

            foreach (Clip m in members)
            {
                if (atStart) m.TrimStart(clamped);
                else m.TrimEnd(clamped);
            }
        }

        public void ExtendStart(TimeSpan amount) => ExtendCore(amount, atStart: true, ripple: false);
        public void ExtendEnd(TimeSpan amount) => ExtendCore(amount, atStart: false, ripple: false);
        public void RippleExtendStart(TimeSpan amount) => ExtendCore(amount, atStart: true, ripple: true);
        public void RippleExtendEnd(TimeSpan amount) => ExtendCore(amount, atStart: false, ripple: true);

        private void ExtendCore(TimeSpan amount, bool atStart, bool ripple)
        {
            if (amount <= TimeSpan.Zero) return;

            List<Clip> members = [.. Members];
            if (members.Count == 0) return;

            //only the HEAD side has a real content ceiling to clamp against
            //(a member's own MaxHeadExtend) — the tail side is unconstrained
            //for every clip type (freeze-frame/hold-last-sample covers it),
            //so only Start-side extends need the "most-constrained member"
            //clamp at all
            TimeSpan clamped = amount;
            if (atStart)
            {
                TimeSpan ceiling = members.Select(m => m.MaxHeadExtend()).Min();
                clamped = ceiling == TimeSpan.MaxValue || amount <= ceiling ? amount : ceiling;
            }

            if (clamped <= TimeSpan.Zero) return;

            foreach (Clip m in members)
            {
                if (atStart)
                {
                    if (ripple) m.RippleExtendStart(clamped);
                    else m.ExtendStart(clamped);
                }
                else
                {
                    if (ripple) m.RippleExtendEnd(clamped);
                    else m.ExtendEnd(clamped);
                }
            }
        }

        // ---------------------------------------------------------------
        // Split — splits every member spanning `at`, then resolves into
        // two groups: everything before keeps this Id, everything at/after
        // gets a fresh one. Matches DaVinci Resolve's own behavior — see
        // the schema doc.
        //
        // BUG FOUND IN THE FIELD (fixed here): the pre-fix version always
        // assigned a fresh Id to the at/after side (and left the before
        // side on the original Id) with no regard for how many members
        // ended up on either side — a group of exactly two members split at
        // a point between them left BOTH sides as a "group" of one clip
        // each, neither of which was ever dissolved. Every other place a
        // group's membership can shrink (Timeline.NotifyClipDetached,
        // Timeline.Link — see its own remarks) auto-dissolves a group once
        // it's down to a single member; Split is now consistent with that.
        // ---------------------------------------------------------------

        public void Split(TimeSpan at)
        {
            foreach (Clip clip in Members)
            {
                if (clip.Start < at && clip.End > at) clip.Split(at);
            }

            //re-derive membership now that splitting (if any) has finished —
            //every member's geometry is settled, so partitioning by current
            //position is enough; no need for Clip.Split to have handed back
            //its fragments
            Guid newGroupId = Guid.NewGuid();

            List<Clip> before = [];
            List<Clip> after = [];

            foreach (Clip clip in Members)
            {
                if (clip.Start >= at)
                {
                    clip.LinkGroupId = newGroupId;
                    after.Add(clip);
                }
                else
                {
                    before.Add(clip);
                }
            }

            if (before.Count == 1) before[0].LinkGroupId = null;
            if (after.Count == 1) after[0].LinkGroupId = null;
        }

        public void Delete()
        {
            foreach (Clip clip in Members) clip.Delete();
        }

        // ---------------------------------------------------------------
        // Convenience factories
        //
        // REWRITE ("clips are graphs"): VideoClip/AudioClip are built via
        // their own static factory methods now, not object initializers —
        // see VideoClip.cs/AudioClip.cs. A VideoSourceNode/AudioSourceNode
        // gets ITS OWN duplicated Source (not a shared reference) for the
        // same reason as before this rewrite: trimming one clip's head
        // independently of the other (see the schema doc's Move section —
        // a member edited alone may desync from the rest) must not
        // silently move the other clip's in-point too, which sharing one
        // Source instance between them would risk.
        // ---------------------------------------------------------------

        /// <summary>
        /// The everyday "drag a video file with audio onto the timeline"
        /// case: split-and-link in one call. Returns unattached clips —
        /// placement onto actual channels is left to the caller.
        /// </summary>
        public static (VideoClip Video, AudioClip Audio) CreateAudioVideoPair(
            Source source, TimeSpan start, TimeSpan duration)
        {
            Guid groupId = Guid.NewGuid();

            VideoClip video = VideoClip.CreateFromSource(source.Duplicate(), start, duration);
            video.LinkGroupId = groupId;

            AudioClip audio = AudioClip.CreateFromSource(source.Duplicate(), start, duration);
            audio.LinkGroupId = groupId;

            return (video, audio);
        }

        /// <summary>Mirrors CreateAudioVideoPair for nested timelines — see the schema doc.</summary>
        public static (VideoClip Video, AudioClip Audio) CreateTimelineAudioVideoPair(
            TimelineReference reference, TimeSpan start, TimeSpan duration)
        {
            Guid groupId = Guid.NewGuid();

            VideoClip video = VideoClip.CreateTimelineEmbed(reference.Duplicate(), start, duration);
            video.LinkGroupId = groupId;

            AudioClip audio = AudioClip.CreateTimelineEmbed(reference.Duplicate(), start, duration);
            audio.LinkGroupId = groupId;

            return (video, audio);
        }
    }
}