using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components.Channels;
using EditSharp.Components.Clips;
using EditSharp.Components.Nodes.Sources;
using EditSharp.Components.Sources.Video;
using EditSharp.Components.Sources.Audio;
using EditSharp.History;

namespace EditSharp.Components
{
    /// <summary>
    /// A sequence of Channels. Also the seat of nested-timeline bookkeeping
    /// (cycle prevention, UsedBy tracking) — see the remarks on
    /// ValidateNoCycle/RegisterEmbeddedTimelines/NotifyClipDetached below.
    ///
    /// REWRITE (\"clips are graphs\"): the old dedicated
    /// AddTimelineClip/RippleAddTimelineClip/AddTimelineClipCore methods are
    /// GONE. Embedding a nested Timeline used to require a special add path
    /// because it was a distinct Clip subtype (TimelineVideoClip/
    /// TimelineAudioClip) that only Timeline knew how to construct
    /// correctly (cycle-checked, UsedBy-registered). Now that embedding a
    /// nested Timeline is just wiring a TimelineVideoInputNode/
    /// TimelineAudioInputNode into an ordinary VideoClip's/AudioClip's
    /// graph (built via VideoClip.CreateTimelineEmbed/AudioClip.CreateTimelineEmbed),
    /// there's nothing left for a separate Timeline-level add method to do
    /// — build the clip, then place it with the SAME Channel.AddClip/
    /// RippleAddClip every other clip uses. Channel.AddClipCore calls back
    /// into ValidateNoCycle/RegisterEmbeddedTimelines itself (see Channel.cs),
    /// generalized to scan the clip's graph for however many embed nodes it
    /// actually has (zero, one, or several) rather than assuming exactly one.
    ///
    /// REWRITE (\"channels split by kind\"): VideoChannels and AudioChannels
    /// used to live together in one mixed `_channels` list, ordered however
    /// they were added. They're now two entirely separate lists. The reason
    /// is LinkGroup.MoveChannel/RippleMoveChannel (see LinkGroup.cs): asking
    /// a linked group to \"move up/down a channel\" only has an unambiguous
    /// meaning per member if each member's own channel index is counted
    /// among channels of its OWN kind — a video clip and its linked audio
    /// clip can never share a channel, so there's no single mixed index that
    /// means the same thing for both of them at once. With two separate
    /// lists, \"move this video clip's channel index by `delta`\" and \"move
    /// this audio clip's channel index by the SAME `delta`\" are just two
    /// independent, symmetric lookups — see ResolveChannelDelta below, which
    /// also creates whichever new channels (of the matching kind) a delta
    /// past the current top requires. VideoChannels keeps its own
    /// bottom-to-top compositing order exactly as before; AudioChannels'
    /// order still carries no acoustic meaning (see AudioMixer).
    /// </summary>
    public sealed class Timeline
    {
        /// <summary>Stable identity, saved with the timeline; nested-timeline sources refer to it.</summary>
        public Guid Id { get; init; } = Guid.NewGuid();

        private readonly List<VideoChannel> _videoChannels = [];
        private readonly List<AudioChannel> _audioChannels = [];

        /// <summary>
        /// Every VideoChannel on this timeline, in compositing order —
        /// index 0 is the bottom layer, each one after it draws on top,
        /// exactly as before this split.
        /// </summary>
        public IReadOnlyList<VideoChannel> VideoChannels => _videoChannels;

        /// <summary>
        /// Every AudioChannel on this timeline. Order has no acoustic
        /// meaning (see AudioMixer's own remarks) — it only affects how
        /// channels are listed in a UI.
        /// </summary>
        public IReadOnlyList<AudioChannel> AudioChannels => _audioChannels;

        /// <summary>
        /// Every channel on this timeline, video channels first (in their
        /// own compositing order) then audio channels — a convenience view
        /// for code that genuinely doesn't care about the split (\"is there
        /// at least one channel at all,\" walking every clip on the
        /// timeline, and so on). Allocates a freshly merged list on every
        /// access, so a caller in a hot per-frame path should prefer
        /// VideoChannels/AudioChannels directly instead — see
        /// FrameStateResolver/AudioMixer, which do exactly that.
        /// </summary>
        public IReadOnlyList<Channel> Channels => [.. _videoChannels, .. _audioChannels];

        public TimeSpan Duration
        {
            get
            {
                TimeSpan max = TimeSpan.Zero;
                foreach (VideoChannel channel in _videoChannels) if (channel.End > max) max = channel.End;
                foreach (AudioChannel channel in _audioChannels) if (channel.End > max) max = channel.End;
                return max;
            }
        }

        //timelines currently embedding THIS one as a nested-timeline child
        //(possibly more than once — see RegisterEmbeddedTimelines)
        private readonly List<Timeline> _usedBy = [];
        public IReadOnlyList<Timeline> UsedBy => _usedBy;

        // ---------------------------------------------------------------
        // Channel management
        // ---------------------------------------------------------------

        public VideoChannel AddChannel(VideoChannel channel)
        {
            Transaction.Apply(() => _videoChannels.Add(channel), () => _videoChannels.Remove(channel), "add channel");
            channel.Timeline = this;
            return channel;
        }

        public AudioChannel AddChannel(AudioChannel channel)
        {
            Transaction.Apply(() => _audioChannels.Add(channel), () => _audioChannels.Remove(channel), "add channel");
            channel.Timeline = this;
            return channel;
        }

        /// <summary>
        /// Runtime-typed fallback for a caller holding only a `Channel`
        /// reference (its concrete type isn't known until this runs) —
        /// dispatches to whichever typed overload above actually matches.
        /// Prefer AddChannel(VideoChannel)/AddChannel(AudioChannel) when the
        /// concrete type is known at the call site; they need no such
        /// dispatch and hand back the concrete type directly.
        /// </summary>
        public Channel AddChannel(Channel channel) => channel switch
        {
            VideoChannel video => AddChannel(video),
            AudioChannel audio => AddChannel(audio),
            _ => throw new ArgumentException(
                $"Unknown channel type {channel.GetType().Name}.", nameof(channel)),
        };

        public void RemoveChannel(Channel channel)
        {
            switch (channel)
            {
                case VideoChannel video: RemoveChannelCore(_videoChannels, video); break;
                case AudioChannel audio: RemoveChannelCore(_audioChannels, audio); break;
                default: throw new ArgumentException(
                    $"Unknown channel type {channel.GetType().Name}.", nameof(channel));
            }

            channel.Timeline = null;
        }

        /// <summary>
        /// The same range taken out of every channel, each gap closed — the
        /// whole timeline gets shorter by the range, so nothing on one
        /// channel drifts against another. A ripple delete that keeps the
        /// tracks in sync, at the cost of whatever else sat in that range.
        /// </summary>
        public void RippleRemoveRange(TimeSpan start, TimeSpan end)
        {
            foreach (Channel channel in Channels) channel.RippleRemoveRange(start, end);
        }

        private static void RemoveChannelCore<T>(List<T> list, T channel) where T : Channel
        {
            int index = list.IndexOf(channel);
            if (index < 0) return;

            Transaction.Apply(
                () => list.Remove(channel),
                () => list.Insert(Math.Min(index, list.Count), channel),
                "remove channel");
        }

        // ---------------------------------------------------------------
        // Channel index / reordering — Channel.Index/MoveUp/MoveDown (see
        // Channel.cs) are the normal way callers reach these; both dispatch
        // on the channel's own kind exactly like AddChannel/RemoveChannel
        // above, since VideoChannels and AudioChannels are separate lists.
        // Reordering only ever swaps two EXISTING channels — unlike
        // ResolveChannelDeltaCore below, it never creates a new one, since
        // there's no "past the top" here: moving the topmost channel up,
        // or the bottommost down, is simply a no-op.
        // ---------------------------------------------------------------

        internal int IndexOf(Channel channel) => channel switch
        {
            VideoChannel video => _videoChannels.IndexOf(video),
            AudioChannel audio => _audioChannels.IndexOf(audio),
            _ => throw new NotSupportedException($"Unknown channel type {channel.GetType().Name}."),
        };

        internal void SwapChannel(Channel channel, int direction)
        {
            switch (channel)
            {
                case VideoChannel video: SwapChannelCore(_videoChannels, video, direction); break;
                case AudioChannel audio: SwapChannelCore(_audioChannels, audio, direction); break;
                default: throw new NotSupportedException($"Unknown channel type {channel.GetType().Name}.");
            }
        }

        private static void SwapChannelCore<T>(List<T> list, T channel, int direction) where T : Channel
        {
            int index = list.IndexOf(channel);
            if (index < 0)
                throw new InvalidOperationException("This channel does not belong to this timeline.");

            int target = index + direction;
            if (target < 0 || target >= list.Count) return; //already at that end — nothing to swap with

            //a swap is its own inverse
            Transaction.Apply(
                () => { (list[index], list[target]) = (list[target], list[index]); },
                () => { (list[index], list[target]) = (list[target], list[index]); },
                "reorder channel");
        }

        // ---------------------------------------------------------------
        // Channel-delta resolution — used by LinkGroup.MoveChannel/
        // RippleMoveChannel (see LinkGroup.cs) to answer \"what channel is
        // `delta` steps away from `current`, among channels of its own
        // kind,\" creating new channels along the way if `delta` reaches
        // past the current top.
        // ---------------------------------------------------------------

        internal Channel ResolveChannelDelta(Channel current, int delta) => current switch
        {
            VideoChannel video => ResolveChannelDeltaCore(_videoChannels, video, delta, static () => new VideoChannel()),
            AudioChannel audio => ResolveChannelDeltaCore(_audioChannels, audio, delta, static () => new AudioChannel()),
            _ => throw new NotSupportedException($"Unknown channel type {current.GetType().Name}."),
        };

        /// <summary>
        /// `current` must already be one of `list`'s own entries (it came
        /// from a placed Clip's own Channel, so it always is in practice).
        /// Moving UP (positive delta) past the current top channel creates
        /// however many new, empty channels of the matching kind are needed
        /// to reach it — the same thing that happens in most editors when
        /// you drag a clip above the last track and a new one appears.
        /// Moving DOWN (negative delta) past the bottom channel has nowhere
        /// to go — there's no way to insert a channel below index 0 without
        /// renumbering every channel already there — so it just clamps at
        /// the bottom channel instead.
        /// </summary>
        private Channel ResolveChannelDeltaCore<T>(List<T> list, T current, int delta, Func<T> factory)
            where T : Channel
        {
            int index = list.IndexOf(current);
            if (index < 0)
                throw new InvalidOperationException("This channel does not belong to this timeline.");

            int target = index + delta;

            while (target >= list.Count)
            {
                T created = factory();
                list.Add(created);
                created.Timeline = this;
            }

            if (target < 0) target = 0;

            return list[target];
        }

        // ---------------------------------------------------------------
        // Linking
        // ---------------------------------------------------------------

        public LinkGroup? GetLinkGroup(Guid? id) => id.HasValue ? new LinkGroup(id.Value, this) : null;

        /// <summary>
        /// Adds every clip in `clips` to one link group (an existing one,
        /// if any of them already belongs to one; otherwise a fresh Id).
        ///
        /// BUG FOUND IN THE FIELD (fixed here): a clip being linked here may
        /// already belong to a DIFFERENT group than the one this call
        /// settles on — e.g. linking [B, C] where B is already grouped with
        /// A (group G1) and C is already grouped with D (group G2) resolves
        /// to G1, silently \"poaching\" C out of G2 and leaving D behind as an
        /// orphaned singleton group (LinkGroupId set, but the sole member).
        /// Every other group-membership change in this file (LinkGroup.Split,
        /// Timeline.NotifyClipDetached) auto-dissolves a group once it's
        /// down to one member; this method used to be the one place that
        /// didn't. Fixed by recording each clip's PRE-reassignment group,
        /// then dissolving any of those old groups left with exactly one
        /// member once the reassignment is done.
        /// </summary>
        public LinkGroup Link(IEnumerable<Clip> clips)
        {
            List<Clip> list = [.. clips];
            Guid groupId = list.Select(c => c.LinkGroupId).FirstOrDefault(g => g.HasValue) ?? Guid.NewGuid();

            HashSet<Guid> oldGroups = [.. list
                .Select(c => c.LinkGroupId)
                .Where(g => g.HasValue && g.Value != groupId)
                .Select(g => g!.Value)];

            foreach (Clip clip in list) clip.LinkGroupId = groupId;

            foreach (Guid oldGroupId in oldGroups)
            {
                List<Clip> remaining = [.. AllClips().Where(c => c.LinkGroupId == oldGroupId)];
                if (remaining.Count == 1) remaining[0].LinkGroupId = null;
            }

            return new LinkGroup(groupId, this);
        }

        internal IEnumerable<Clip> AllClips() =>
            _videoChannels.SelectMany(c => c.Clips).Concat(_audioChannels.SelectMany(c => c.Clips));

        /// <summary>
        /// Called by Channel.RemoveClip whenever a clip TRULY leaves this
        /// timeline for good (not a transient mid-Move relocation, which
        /// doesn't call this — see Channel.Move's own remarks on the
        /// cross-Timeline known gap). Two jobs: LinkGroup auto-dissolve, and
        /// UsedBy bookkeeping for every embedded-timeline node the clip's
        /// graph happens to contain.
        /// </summary>
        internal void NotifyClipDetached(Clip clip)
        {
            if (clip.LinkGroupId is { } groupId)
            {
                int remaining = AllClips().Count(c => c.LinkGroupId == groupId);
                if (remaining == 1)
                {
                    Clip? lastMember = AllClips().FirstOrDefault(c => c.LinkGroupId == groupId);
                    if (lastMember != null) lastMember.LinkGroupId = null;
                }
            }

            UnregisterEmbeddedTimelines(clip);
        }

        // ---------------------------------------------------------------
        // Nested-timeline cycle prevention / UsedBy bookkeeping — GENERIC
        // over however many TimelineVideoInputNode/TimelineAudioInputNode
        // nodes a clip's graph contains (see this class's own remarks).
        // ---------------------------------------------------------------

        /// <summary>Throws if placing `clip` on this timeline would create a nested-timeline cycle.</summary>
        internal void ValidateNoCycle(Clip clip)
        {
            foreach (Timeline embedded in EmbeddedTimelinesOf(clip))
            {
                if (WouldCreateCycle(embedded))
                    throw new InvalidOperationException(
                        "This clip embeds a Timeline that would create a cycle (directly or through a " +
                        "chain of embeddings) if placed here.");
            }
        }

        internal void RegisterEmbeddedTimelines(Clip clip)
        {
            foreach (Timeline embedded in EmbeddedTimelinesOf(clip))
                Transaction.Apply(() => embedded._usedBy.Add(this), () => embedded._usedBy.Remove(this), "register embed");
        }

        private void UnregisterEmbeddedTimelines(Clip clip)
        {
            foreach (Timeline embedded in EmbeddedTimelinesOf(clip))
            {
                if (!embedded._usedBy.Contains(this)) continue;

                Transaction.Apply(() => embedded._usedBy.Remove(this), () => embedded._usedBy.Add(this), "unregister embed");
            }
        }

        private static IEnumerable<Timeline> EmbeddedTimelinesOf(Clip clip)
        {
            foreach (VideoSourceNode node in clip.Graph.AllNodes.OfType<VideoSourceNode>())
                if (node.Source is TimelineVideoSource { Timeline: { } embedded })
                    yield return embedded;

            foreach (AudioSourceNode node in clip.Graph.AllNodes.OfType<AudioSourceNode>())
                if (node.Source is TimelineAudioSource { Timeline: { } embedded })
                    yield return embedded;
        }

        /// <summary>
        /// True if `child` is reachable by walking UP this timeline's own
        /// ancestry (this.UsedBy, then each of those timelines' own UsedBy,
        /// breadth-first) — including `child == this` directly. Walks the
        /// (typically shallow) ancestor chain rather than searching all of
        /// `child`'s descendants, by design.
        /// </summary>
        private bool WouldCreateCycle(Timeline child)
        {
            if (ReferenceEquals(child, this)) return true;

            var visited = new HashSet<Timeline>();
            var queue = new Queue<Timeline>(_usedBy);

            while (queue.Count > 0)
            {
                Timeline current = queue.Dequeue();
                if (ReferenceEquals(current, child)) return true;
                if (!visited.Add(current)) continue;

                foreach (Timeline ancestor in current._usedBy) queue.Enqueue(ancestor);
            }

            return false;
        }
    }
}