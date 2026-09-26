using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components;
using EditSharp.Components.Channels;
using EditSharp.Components.Clips;
using EditSharp.Components.Nodes;
using EditSharp.History;

namespace EditSharp.Audio.Engine
{
    /// <summary>
    /// Renders a timeline's audio a block at a time. Each call snapshots the
    /// timeline's structure under ModelLock (which clips are audible, each
    /// graph's wiring), runs every audible clip's ClipAudioNetwork over the
    /// part of the block it covers, sums clips into their channel's bus,
    /// applies the channel's volume, and sums channels into the master.
    ///
    /// Clips starting within EditSharpConfig.SourceLookahead get their
    /// network built and their sources preparing before they're heard; a
    /// clip's network is dropped once it's neither audible nor upcoming.
    /// Taps see every node, channel (by Channel.Id) and the master
    /// (AudioTap.Master); a nested timeline's renderer is not the master and
    /// doesn't publish it.
    /// </summary>
    internal sealed class TimelineAudioRenderer(Timeline timeline, AudioSession session, bool isMaster = true) : IDisposable
    {
        private readonly Dictionary<AudioClip, ClipAudioNetwork> _networks = new(ReferenceEqualityComparer.Instance);
        private float[] _bus = new float[session.BlockFrames * session.Format.Channels];

        private readonly record struct Audible(AudioClip Clip, long Start, long End, Rational Speed, PitchPreservation Pitch, Graph Graph);

        private readonly record struct ChannelWork(Guid Id, float Volume, List<Audible> Clips);

        /// <summary>Renders timeline frames [frame, frame + master.Length / channels) into `master`, overwriting it.</summary>
        public void Render(long frame, Span<float> master)
        {
            AudioFormat format = session.Format;
            int channels = format.Channels;
            int frames = master.Length / channels;
            long end = frame + frames;
            long lookahead = session.FrameOf(EditSharpConfig.SourceLookahead);

            master.Clear();

            var work = new List<ChannelWork>();
            var upcoming = new List<(AudioClip Clip, Graph Graph)>();

            using (ModelLock.Read())
            {
                foreach (AudioChannel channel in timeline.AudioChannels)
                {
                    var audible = new List<Audible>();

                    foreach (Clip clip in channel.Clips)
                    {
                        if (clip is not AudioClip audio) continue;

                        long start = session.FrameOf(clip.Start), stop = session.FrameOf(clip.End);

                        if (start < end && stop > frame)
                            audible.Add(new Audible(audio, start, stop, clip.Speed, audio.PreservePitch, audio.Graph.Snapshot()));
                        else if (start >= end && start < end + lookahead)
                            upcoming.Add((audio, audio.Graph.Snapshot()));
                    }

                    work.Add(new ChannelWork(channel.Id, channel.Volume, audible));
                }
            }

            foreach ((AudioClip clip, Graph graph) in upcoming) Network(clip).Prepare(graph);

            if (_bus.Length < master.Length) _bus = new float[master.Length];

            foreach (ChannelWork channel in work)
            {
                Span<float> bus = _bus.AsSpan(0, master.Length);
                bus.Clear();

                foreach (Audible clip in channel.Clips)
                {
                    long from = Math.Max(frame, clip.Start), to = Math.Min(end, clip.End);
                    int count = (int)(to - from);

                    var tick = new AudioTick(
                        format, from, count,
                        Time.FromSamples(from - clip.Start, format.SampleRate) * clip.Speed,
                        (from - clip.Start) * clip.Speed.Value,
                        clip.Speed,
                        clip.Graph,
                        clip.Pitch);

                    Network(clip.Clip).Process(tick, bus.Slice((int)(from - frame) * channels, count * channels));
                }

                if (channel.Volume != 1f)
                    for (int i = 0; i < bus.Length; i++) bus[i] *= channel.Volume;

                if (session.Taps.Has(channel.Id))
                    session.Taps.Publish(channel.Id, _bus, bus.Length, format, session.TimeOf(frame));

                for (int i = 0; i < bus.Length; i++) master[i] += bus[i];
            }

            if (isMaster && session.Taps.Has(AudioTap.Master))
            {
                float[] copy = master.ToArray();
                session.Taps.Publish(AudioTap.Master, copy, copy.Length, format, session.TimeOf(frame));
            }

            //let go of clips that are neither audible nor coming up
            var keep = new HashSet<AudioClip>(work.SelectMany(w => w.Clips).Select(c => c.Clip).Concat(upcoming.Select(u => u.Clip)), ReferenceEqualityComparer.Instance);
            foreach (AudioClip gone in _networks.Keys.Where(c => !keep.Contains(c)).ToList())
            {
                _networks[gone].Dispose();
                _networks.Remove(gone);
            }
        }

        /// <summary>Prepares every clip audible at `frame`, waiting until each can be read.</summary>
        public void PrepareAt(long frame)
        {
            var audible = new System.Collections.Generic.List<(AudioClip Clip, Graph Graph)>();

            using (ModelLock.Read())
            {
                foreach (AudioChannel channel in timeline.AudioChannels)
                    foreach (Clip clip in channel.Clips)
                        if (clip is AudioClip audio && session.FrameOf(clip.Start) <= frame && session.FrameOf(clip.End) > frame)
                            audible.Add((audio, audio.Graph.Snapshot()));
            }

            foreach ((AudioClip clip, Graph graph) in audible) Network(clip).Prepare(graph, wait: true);
        }

        private ClipAudioNetwork Network(AudioClip clip)
        {
            if (!_networks.TryGetValue(clip, out ClipAudioNetwork? network))
                _networks[clip] = network = new ClipAudioNetwork(clip, session);
            return network;
        }

        public void Dispose()
        {
            foreach (ClipAudioNetwork network in _networks.Values) network.Dispose();
            _networks.Clear();
        }
    }
}
