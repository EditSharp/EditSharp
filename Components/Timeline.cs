using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components.Clips;
using EditSharp.Components.Nodes.Sources.Video;
using EditSharp.Components.Nodes.Sources.Audio;
 
namespace EditSharp.Components
{
    /// <summary>
    /// A sequence of Channels. Also the seat of nested-timeline bookkeeping
    /// (cycle prevention, UsedBy tracking) — see the remarks on
    /// ValidateNoCycle/RegisterEmbeddedTimelines/NotifyClipDetached below.
    ///
    /// REWRITE ("clips are graphs"): the old dedicated
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
    /// </summary>
    public sealed class Timeline
    {
        private readonly List<Channel> _channels = [];
        public IReadOnlyList<Channel> Channels => _channels;
 
        public TimeSpan Duration => _channels.Count == 0 ? TimeSpan.Zero : _channels.Max(c => c.End);
 
        //timelines currently embedding THIS one as a nested-timeline child
        //(possibly more than once — see RegisterEmbeddedTimelines)
        private readonly List<Timeline> _usedBy = [];
        public IReadOnlyList<Timeline> UsedBy => _usedBy;
 
        public Channel AddChannel(Channel channel)
        {
            _channels.Add(channel);
            channel.Timeline = this;
            return channel;
        }
 
        public void RemoveChannel(Channel channel)
        {
            _channels.Remove(channel);
            channel.Timeline = null;
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
        /// to G1, silently "poaching" C out of G2 and leaving D behind as an
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
 
        internal IEnumerable<Clip> AllClips() => _channels.SelectMany(c => c.Clips);
 
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
                embedded._usedBy.Add(this);
        }
 
        private void UnregisterEmbeddedTimelines(Clip clip)
        {
            foreach (Timeline embedded in EmbeddedTimelinesOf(clip))
                embedded._usedBy.Remove(this);
        }
 
        private static IEnumerable<Timeline> EmbeddedTimelinesOf(Clip clip)
        {
            foreach (TimelineVideoInputNode node in clip.Graph.Nodes.OfType<TimelineVideoInputNode>())
                yield return node.Reference.Timeline;
 
            foreach (TimelineAudioInputNode node in clip.Graph.Nodes.OfType<TimelineAudioInputNode>())
                yield return node.Reference.Timeline;
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
 