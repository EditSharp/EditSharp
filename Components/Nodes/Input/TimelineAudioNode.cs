using EditSharp.Components.Media;
using System;
using System.Threading;
using System.Threading.Tasks;
using EditSharp.Audio.Engine;
using EditSharp.Editing;
using EditSharp.History;

namespace EditSharp.Components.Nodes.Input
{
    /// <summary>Another timeline's mixed audio.</summary>
    /// <remarks>The nested timeline is mixed block by block by its own TimelineAudioRenderer, in the reading session. It's saved as the timeline's Id; see <see cref="ComponentSerializer.Deserialize{T}"/>.</remarks>
    //unlisted until the GUI has a timeline picker
    [NodeKind("timeline-audio", DisplayName = "Timeline", Listed = false)]
    public sealed class TimelineAudioNode : AudioInputNode
    {
        Timeline? _timeline;
        /// <summary>The timeline to play; null plays nothing and reports the input offline.</summary>
        [Editable("Timeline")]
        public Timeline? Timeline
        {
            get => _timeline;
            set
            {
                Components.Timeline.Reembed(OwnerClip, _timeline, value);
                Transaction.Set(this, ref _timeline, value, static (o, v) => o._timeline = v);
                EndMayHaveMoved();
            }
        }

        /// <summary>The timeline's duration.</summary>
        /// <param name="ct">Unused.</param>
        /// <returns>The duration; null when no timeline is chosen.</returns>
        public override Task<TimeSpan?> GetNaturalLengthAsync(CancellationToken ct = default) => Task.FromResult(Timeline?.Duration);

        internal override Task<IPreparedAudioSource> PrepareAsync(CancellationToken ct = default) => Timeline is { } timeline
            ? Task.FromResult<IPreparedAudioSource>(new Prepared(this, timeline))
            : Task.FromException<IPreparedAudioSource>(new SourceUnavailableException(SourceUnavailableReason.NoTimeline, "No timeline is selected."));

        /// <inheritdoc/>
        protected internal override Timeline? ReferencedTimeline(Guid id) => Timeline?.Id == id ? Timeline : null;

        private sealed class Prepared(TimelineAudioNode node, Timeline timeline) : IPreparedAudioSource
        {
            public IAudioSampleReader OpenReader(AudioReaderOptions options) => new Reader(node, timeline, options);

            public void Dispose() { }
        }

        private sealed class Reader : IAudioSampleReader
        {
            private readonly TimelineAudioNode _node;
            private readonly Timeline _timeline;
            private readonly AudioSession _session;
            private readonly TimelineAudioRenderer _renderer;
            private long _position;
            private bool _ended;

            public Reader(TimelineAudioNode node, Timeline timeline, AudioReaderOptions options)
            {
                _node = node;
                _timeline = timeline;

                //the reading session's taps, report and wait policy reach into the nested timeline
                _session = options.Session ?? new AudioSession(new AudioFormat(options.SampleRate, options.Channels), waitForSources: true);
                _renderer = new TimelineAudioRenderer(timeline, _session, isMaster: false);
                _position = _session.FrameOf(options.StartAt);
            }

            public int Read(Span<float> destination)
            {
                if (_ended) throw new SourceUnavailableException(SourceUnavailableReason.EndOfSource, "The nested timeline has ended.");

                int channels = _session.Format.Channels;
                int frames = destination.Length / channels;
                (TimeSpan from, TimeSpan? length) = _node.ResolveWindow(_timeline.Duration);
                long start = _session.FrameOf(from);
                long window = _session.FrameOf(length ?? TimeSpan.Zero);

                int done = 0;
                while (done < frames)
                {
                    if (_position >= window)
                    {
                        if (!_node.Loop || window <= 0) { _ended = true; break; }
                        _position %= window;
                    }

                    int block = (int)System.Math.Min(System.Math.Min(_session.BlockFrames, frames - done), window - _position);
                    _renderer.Render(start + _position, destination.Slice(done * channels, block * channels));
                    done += block;
                    _position += block;
                }

                return done;
            }

            public void Dispose() => _renderer.Dispose();
        }
    }
}
