using EditSharp.Components.Media;
using System;
using EditSharp.Components.Nodes;
using EditSharp.Components.Nodes.Input;
using EditSharp.Editing;
using EditSharp.History;

namespace EditSharp.Components.Clips
{
    /// <summary>A clip that plays sound: its graph's inputs, through its effects.</summary>
    /// <remarks>It goes on an <see cref="Channels.AudioChannel"/>. The factories build the usual graph: the input, then a gain.</remarks>
    public sealed class AudioClip : Clip
    {
        private readonly Graph _graph;
        /// <inheritdoc/>
        public override Graph Graph => _graph;

        PitchPreservation _preservePitch = PitchPreservation.WSOLA;
        /// <summary>How the clip keeps its pitch when Speed isn't 1x.</summary>
        [Editable("Preserve pitch")]
        public PitchPreservation PreservePitch { get => _preservePitch; set => Transaction.Set(this, ref _preservePitch, value, static (o, v) => o._preservePitch = v); }

        private AudioClip(Graph graph)
        {
            _graph = graph;
            graph.Clip = this;
        }

        /// <summary>A clip playing a media.</summary>
        /// <remarks>The clip isn't placed; add it to a channel. Nothing is recorded in history.</remarks>
        /// <param name="media">What the clip plays.</param>
        /// <param name="start">Where the clip starts on the timeline.</param>
        /// <param name="duration">How long it lasts.</param>
        /// <returns>The clip.</returns>
        public static AudioClip CreateFromMedia(AudioMedia media, Time start, Time duration) =>
            CreateFromInput(Transaction.Suppressed(() => new AudioMediaNode { Media = media }), start, duration);

        /// <summary>A clip playing an input node of any kind, such as a generator you've set up.</summary>
        /// <remarks>The clip isn't placed; add it to a channel. Nothing is recorded in history.</remarks>
        /// <param name="input">Where the sound comes from.</param>
        /// <param name="start">Where the clip starts on the timeline.</param>
        /// <param name="duration">How long it lasts.</param>
        /// <returns>The clip.</returns>
        public static AudioClip CreateFromInput(AudioInputNode input, Time start, Time duration) =>
            Transaction.Suppressed(() => new AudioClip(Graph.CreateAudioGraph(input)) { Start = start, Duration = duration });

        /// <summary>A clip playing a synthesized tone; see <see cref="ToneNode"/>.</summary>
        /// <remarks>The clip isn't placed; add it to a channel. Nothing is recorded in history.</remarks>
        /// <param name="start">Where the clip starts on the timeline.</param>
        /// <param name="duration">How long it lasts.</param>
        /// <param name="waveform">The shape of the wave.</param>
        /// <param name="frequencyHz">The pitch, in hertz.</param>
        /// <param name="amplitude">The volume, from 0 (silent) to 1 (full scale).</param>
        /// <returns>The clip.</returns>
        public static AudioClip CreateTone(
            Time start, Time duration, Waveform waveform = Waveform.Sine,
            float frequencyHz = 440f, float amplitude = 1f) =>
            CreateFromInput(Transaction.Suppressed(() => new ToneNode { Waveform = waveform, Frequency = new(frequencyHz), Amplitude = new(amplitude) }), start, duration);

        /// <summary>A clip playing another timeline's mixed audio.</summary>
        /// <remarks>The clip isn't placed; add it to a channel. Nothing is recorded in history.</remarks>
        /// <param name="timeline">The timeline to play.</param>
        /// <param name="start">Where the clip starts on the timeline.</param>
        /// <param name="duration">How long it lasts.</param>
        /// <returns>The clip.</returns>
        public static AudioClip CreateTimelineEmbed(Timeline timeline, Time start, Time duration) =>
            CreateFromInput(Transaction.Suppressed(() => new TimelineAudioNode { Timeline = timeline }), start, duration);

        /// <summary>A clip with a graph you've built, such as one with several inputs mixed together.</summary>
        /// <remarks>The clip isn't placed; add it to a channel. Nothing is recorded in history.</remarks>
        /// <param name="graph">The clip's graph; start one with <see cref="Graph.CreateEmptyAudioGraph"/>.</param>
        /// <param name="start">Where the clip starts on the timeline.</param>
        /// <param name="duration">How long it lasts.</param>
        /// <returns>The clip.</returns>
        /// <exception cref="ArgumentException"><paramref name="graph"/> isn't an audio graph.</exception>
        public static AudioClip CreateCustom(Graph graph, Time start, Time duration)
        {
            if (graph.Domain != NodeDomain.Audio)
                throw new ArgumentException("AudioClip requires an Audio-domain Graph.", nameof(graph));

            return Transaction.Suppressed(() => new AudioClip(graph) { Start = start, Duration = duration });
        }

        /// <inheritdoc/>
        public override AudioClip Duplicate() => Transaction.Suppressed(() => new AudioClip(Graph.Duplicate()) { Name = Name, Start = Start, Duration = Duration, Speed = Speed, Color = Color, PreservePitch = PreservePitch });
    }
}
