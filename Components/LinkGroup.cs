using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components.Channels;
using EditSharp.Components.Clips;
using EditSharp.History;

namespace EditSharp.Components
{
    /// <summary>Clips linked together, so edits apply to all of them.</summary>
    /// <remarks>Membership comes from the clips' <see cref="Clip.LinkGroupId"/>, read fresh on every use, so any LinkGroup with the same <see cref="Id"/> on the same timeline is the same group. A clip is in at most one group. Get one from <see cref="Timeline.Link"/> or <see cref="Timeline.GetLinkGroup"/>.</remarks>
    public sealed class LinkGroup : ITimelineEditable
    {
        /// <summary>The group's id, as its members' <see cref="Clip.LinkGroupId"/> hold it.</summary>
        public Guid Id { get; }
        private readonly Timeline _timeline;

        internal LinkGroup(Guid id, Timeline timeline)
        {
            Id = id;
            _timeline = timeline;
        }

        /// <summary>The clips in the group, as a new list.</summary>
        public IReadOnlyList<Clip> Members => [.. _timeline.AllClips().Where(c => c.LinkGroupId == Id)];

        /// <inheritdoc/>
        /// <exception cref="NotSupportedException"><paramref name="targetChannel"/> isn't null; use <see cref="MoveChannel"/>.</exception>
        public void Move(TimeSpan newStart, Channel? targetChannel = null) => MoveCore(newStart, targetChannel, ripple: false);
        /// <inheritdoc/>
        /// <exception cref="NotSupportedException"><paramref name="targetChannel"/> isn't null; use <see cref="RippleMoveChannel"/>.</exception>
        public void RippleMove(TimeSpan newStart, Channel? targetChannel = null) => MoveCore(newStart, targetChannel, ripple: true);

        private void MoveCore(TimeSpan newStart, Channel? targetChannel, bool ripple)
        {
            if (targetChannel != null)
                throw new NotSupportedException(
                    "A linked group's members are on channels of different kinds, so it can't move to one channel; " +
                    "use MoveChannel or RippleMoveChannel.");

            List<Clip> members = [.. Members];
            if (members.Count == 0) return;

            TimeSpan delta = newStart - members.Min(m => m.Start);

            //every target first, so moving one member can't change another's
            List<(Clip Clip, TimeSpan NewStart)> targets = [.. members.Select(m => (m, m.Start + delta))];

            foreach ((Clip clip, TimeSpan target) in targets)
            {
                if (ripple) clip.RippleMove(target);
                else clip.Move(target);
            }
        }

        /// <summary>Moves every member up or down by the same number of channels of its own kind, keeping its start.</summary>
        /// <remarks>Moving past the top channel adds channels; moving past the bottom stops at the bottom channel.</remarks>
        /// <param name="delta">How many channels to move: positive up, negative down.</param>
        /// <exception cref="InvalidOperationException">A member isn't placed.</exception>
        public void MoveChannel(int delta) => MoveChannelCore(delta, ripple: false);
        /// <summary>Like <see cref="MoveChannel"/>, but clips at each destination move later instead of being overwritten.</summary>
        /// <param name="delta">How many channels to move: positive up, negative down.</param>
        /// <exception cref="InvalidOperationException">A member isn't placed.</exception>
        public void RippleMoveChannel(int delta) => MoveChannelCore(delta, ripple: true);

        private void MoveChannelCore(int delta, bool ripple)
        {
            if (delta == 0) return;

            List<Clip> members = [.. Members];
            if (members.Count == 0) return;

            //every target channel first, adding any needed, so moving one member can't change another's
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

        /// <inheritdoc/>
        public void TrimStart(TimeSpan amount) => TrimCore(amount, atStart: true);
        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public void ExtendStart(TimeSpan amount) => ExtendCore(amount, atStart: true, ripple: false);
        /// <inheritdoc/>
        public void ExtendEnd(TimeSpan amount) => ExtendCore(amount, atStart: false, ripple: false);
        /// <inheritdoc/>
        public void RippleExtendStart(TimeSpan amount) => ExtendCore(amount, atStart: true, ripple: true);
        /// <inheritdoc/>
        public void RippleExtendEnd(TimeSpan amount) => ExtendCore(amount, atStart: false, ripple: true);

        private void ExtendCore(TimeSpan amount, bool atStart, bool ripple)
        {
            if (amount <= TimeSpan.Zero) return;

            List<Clip> members = [.. Members];
            if (members.Count == 0) return;

            //linked clips move together, so the most constrained member sets the
            //limit for all of them (timeline time, like `amount`)
            TimeSpan ceiling = members.Select(m => atStart ? m.HeadExtendLimit : m.TailExtendLimit).Min();
            TimeSpan clamped = ceiling == TimeSpan.MaxValue || amount <= ceiling ? amount : ceiling;

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

        /// <inheritdoc/>
        public void Split(TimeSpan at)
        {
            foreach (Clip clip in Members)
            {
                if (clip.Start < at && clip.End > at) clip.Split(at);
            }

            //with every member settled, split the group by position
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

        /// <inheritdoc/>
        public void Delete()
        {
            foreach (Clip clip in Members) clip.Delete();
        }

        /// <summary>Dissolves the group: every member is left unlinked, where it is.</summary>
        public void Unlink()
        {
            foreach (Clip clip in Members) clip.LinkGroupId = null;
        }

        /// <summary>Takes one clip out of the group; a group left with one member is dissolved.</summary>
        /// <param name="clip">The clip to unlink.</param>
        public void Remove(Clip clip)
        {
            ArgumentNullException.ThrowIfNull(clip);
            if (clip.LinkGroupId != Id) return;

            clip.LinkGroupId = null;
            if (Members.Count == 1) Unlink();
        }

        /// <summary>A linked video and audio clip that both play one timeline.</summary>
        /// <remarks>The clips aren't placed; add each to a channel of its kind. Each has its own source, so trimming one alone doesn't move the other's in-point. Nothing is recorded in history.</remarks>
        /// <param name="timeline">The timeline to play.</param>
        /// <param name="start">Where the clips start.</param>
        /// <param name="duration">How long they last.</param>
        /// <returns>The two clips.</returns>
        public static (VideoClip Video, AudioClip Audio) CreateTimelineAudioVideoPair(
            Timeline timeline, TimeSpan start, TimeSpan duration)
        {
            using var _ = Transaction.Suppress();

            Guid groupId = Guid.NewGuid();

            VideoClip video = VideoClip.CreateTimelineEmbed(timeline, start, duration);
            video.LinkGroupId = groupId;

            AudioClip audio = AudioClip.CreateTimelineEmbed(timeline, start, duration);
            audio.LinkGroupId = groupId;

            return (video, audio);
        }
    }
}