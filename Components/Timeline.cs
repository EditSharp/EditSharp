using EditSharp.Components.Nodes;
using EditSharp.Components.Nodes.Input;
using EditSharp.Components;
using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components.Channels;
using EditSharp.Components.Clips;
using EditSharp.History;

namespace EditSharp.Components
{
    /// <summary>A sequence: video channels drawn bottom to top, and audio channels mixed together.</summary>
    /// <remarks>
    /// Video and audio channels are kept in two lists, each indexed from 0 at the
    /// bottom, so a linked video and audio clip can move up or down by the same
    /// number of channels. A timeline can be embedded in another through a
    /// <see cref="Nodes.Input.TimelineVideoNode"/> or <see cref="Nodes.Input.TimelineAudioNode"/>;
    /// an edit that would make a timeline contain itself is refused with
    /// InvalidOperationException.
    /// </remarks>
    public sealed class Timeline
    {
        /// <summary>Identifies the timeline; it's saved, and timeline sources refer to it.</summary>
        public Guid Id { get; init; } = Guid.NewGuid();

        private readonly List<VideoChannel> _videoChannels = [];
        private readonly List<AudioChannel> _audioChannels = [];

        /// <summary>The video channels, bottom first: each draws over the ones before it.</summary>
        public IReadOnlyList<VideoChannel> VideoChannels => _videoChannels;

        /// <summary>The audio channels; their order only affects how they're listed.</summary>
        public IReadOnlyList<AudioChannel> AudioChannels => _audioChannels;

        /// <summary>Every channel: the video channels, then the audio channels.</summary>
        /// <remarks>A new list on every read; code that runs every frame should use <see cref="VideoChannels"/> and <see cref="AudioChannels"/>.</remarks>
        public IReadOnlyList<Channel> Channels => [.. _videoChannels, .. _audioChannels];

        /// <summary>Where the last clip on any channel ends; zero when there are none.</summary>
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

        private readonly List<Timeline> _usedBy = [];

        /// <summary>The timelines that embed this one, once per clip source that embeds it.</summary>
        public IReadOnlyList<Timeline> UsedBy => _usedBy;

        // ---- channel management ----

        /// <summary>Adds a video channel at the top.</summary>
        /// <param name="channel">The channel; any clips on it come too.</param>
        /// <returns><paramref name="channel"/>.</returns>
        /// <exception cref="InvalidOperationException">A clip on it embeds a timeline that contains this one.</exception>
        public VideoChannel AddChannel(VideoChannel channel)
        {
            foreach (Clip clip in channel.Clips) ValidateNoCycle(clip);

            Transaction.Apply(() => _videoChannels.Add(channel), () => _videoChannels.Remove(channel), "add channel");
            channel.Timeline = this;

            foreach (Clip clip in channel.Clips) RegisterEmbeddedTimelines(clip);
            return channel;
        }

        /// <summary>Adds a audio channel at the top.</summary>
        /// <param name="channel">The channel; any clips on it come too.</param>
        /// <returns><paramref name="channel"/>.</returns>
        /// <exception cref="InvalidOperationException">A clip on it embeds a timeline that contains this one.</exception>
        public AudioChannel AddChannel(AudioChannel channel)
        {
            foreach (Clip clip in channel.Clips) ValidateNoCycle(clip);

            Transaction.Apply(() => _audioChannels.Add(channel), () => _audioChannels.Remove(channel), "add channel");
            channel.Timeline = this;

            foreach (Clip clip in channel.Clips) RegisterEmbeddedTimelines(clip);
            return channel;
        }

        /// <summary>Adds a channel of either kind at the top of its kind.</summary>
        /// <param name="channel">The channel; any clips on it come too.</param>
        /// <returns><paramref name="channel"/>.</returns>
        /// <exception cref="ArgumentException">The channel is neither a video nor an audio channel.</exception>
        /// <exception cref="InvalidOperationException">A clip on it embeds a timeline that contains this one.</exception>
        public Channel AddChannel(Channel channel) => channel switch
        {
            VideoChannel video => AddChannel(video),
            AudioChannel audio => AddChannel(audio),
            _ => throw new ArgumentException(
                $"Unknown channel type {channel.GetType().Name}.", nameof(channel)),
        };

        /// <summary>Removes a channel; its clips stay on it.</summary>
        /// <param name="channel">The channel.</param>
        /// <exception cref="ArgumentException">The channel is neither a video nor an audio channel.</exception>
        public void RemoveChannel(Channel channel)
        {
            switch (channel)
            {
                case VideoChannel video: RemoveChannelCore(_videoChannels, video); break;
                case AudioChannel audio: RemoveChannelCore(_audioChannels, audio); break;
                default: throw new ArgumentException(
                    $"Unknown channel type {channel.GetType().Name}.", nameof(channel));
            }

            foreach (Clip clip in channel.Clips) UnregisterEmbeddedTimelines(clip);
            channel.Timeline = null;
        }

        /// <summary>Clears a range on every channel and closes the gap, so the channels stay in sync.</summary>
        /// <param name="start">Where the range starts.</param>
        /// <param name="end">Where it ends; an empty or reversed range does nothing.</param>
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
            if (target < 0 || target >= list.Count) return; //already at that end

            //a swap is its own inverse
            Transaction.Apply(
                () => { (list[index], list[target]) = (list[target], list[index]); },
                () => { (list[index], list[target]) = (list[target], list[index]); },
                "reorder channel");
        }

        internal Channel ResolveChannelDelta(Channel current, int delta) => current switch
        {
            VideoChannel video => ResolveChannelDeltaCore(_videoChannels, video, delta, static () => new VideoChannel()),
            AudioChannel audio => ResolveChannelDeltaCore(_audioChannels, audio, delta, static () => new AudioChannel()),
            _ => throw new NotSupportedException($"Unknown channel type {current.GetType().Name}."),
        };

        //the channel `delta` above `current` among its kind: new channels are added past the top, and the
        //bottom channel is as low as it goes
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
                Transaction.Apply(() => list.Add(created), () => list.Remove(created), "add channel");
                created.Timeline = this;
            }

            if (target < 0) target = 0;

            return list[target];
        }

        // ---- linking ----

        /// <summary>The link group with an id.</summary>
        /// <param name="id">A clip's <see cref="Clip.LinkGroupId"/>.</param>
        /// <returns>The group; null when <paramref name="id"/> is null.</returns>
        public LinkGroup? GetLinkGroup(Guid? id) => id.HasValue ? new LinkGroup(id.Value, this) : null;

        /// <summary>Links clips, so group edits apply to all of them.</summary>
        /// <remarks>If any clip is already linked, the others join its group; a group left with one member by this is unlinked.</remarks>
        /// <param name="clips">The clips to link.</param>
        /// <returns>The group.</returns>
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

        //a clip leaving the timeline for good: unlinks a group it leaves with one member, and drops its embeds
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

        //throws if placing `clip` here would make a timeline contain itself
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

        internal void UnregisterEmbeddedTimelines(Clip clip)
        {
            foreach (Timeline embedded in EmbeddedTimelinesOf(clip))
            {
                if (!embedded._usedBy.Contains(this)) continue;

                Transaction.Apply(() => embedded._usedBy.Remove(this), () => embedded._usedBy.Add(this), "unregister embed");
            }
        }

        private static IEnumerable<Timeline> EmbeddedTimelinesOf(Clip clip) => clip.Graph.AllNodes.SelectMany(EmbeddedIn);

        //the timelines a node embeds, through any composite
        internal static IEnumerable<Timeline> EmbeddedIn(Nodes.Node node) => node switch
        {
            TimelineVideoNode { Timeline: { } embedded } => [embedded],
            TimelineAudioNode { Timeline: { } embedded } => [embedded],
            Nodes.CompositeNode composite => composite.Inner.AllNodes.SelectMany(EmbeddedIn),
            _ => [],
        };

        //a placed clip's embed changing from `removed` to `added`; see the other overload
        internal static void Reembed(Clip? clip, Timeline? removed, Timeline? added) =>
            Reembed(clip, removed is null ? [] : [removed], added is null ? [] : [added]);

        //a placed clip's embeds changing: rejects a cycle before anything changes, then moves the UsedBy entries
        internal static void Reembed(Clip? clip, IEnumerable<Timeline> removed, IEnumerable<Timeline> added)
        {
            if (clip?.Channel?.Timeline is not { } host) return;

            List<Timeline> adding = [.. added];
            foreach (Timeline embedded in adding)
            {
                if (host.WouldCreateCycle(embedded))
                    throw new InvalidOperationException(
                        "This would embed a Timeline in itself (directly or through a chain of embeddings).");
            }

            foreach (Timeline embedded in removed)
            {
                if (!embedded._usedBy.Contains(host)) continue;
                Transaction.Apply(() => embedded._usedBy.Remove(host), () => embedded._usedBy.Add(host), "unregister embed");
            }

            foreach (Timeline embedded in adding)
                Transaction.Apply(() => embedded._usedBy.Add(host), () => embedded._usedBy.Remove(host), "register embed");
        }

        //whether `child` is this timeline or one that (through any chain) embeds it, found by walking up UsedBy
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