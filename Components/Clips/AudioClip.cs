using System;
using EditSharp.Components.Nodes;
using EditSharp.Components.Nodes.Sources;
using EditSharp.Editing;
using EditSharp.History;
using EditSharp.Components;
using EditSharp.Components.Sources.Audio;

namespace EditSharp.Components.Clips
{
    /// <summary>
    /// The only concrete audible Clip type — mirrors VideoClip exactly, one
    /// domain over. What used to be AudioClip's own flat Source property
    /// and the distinct TimelineAudioClip Clip subtype are now InputNode
    /// types wired into this class's single Audio-domain Graph — see
    /// the static factory methods below and
    /// EditSharp.Components.Nodes.Sources. Also new
    /// in this rewrite: CreateTone, wrapping the brand-new
    /// ToneGeneratorInputNode (there was no synthesized-tone clip type at
    /// all before this).
    /// </summary>
    public sealed class AudioClip : Clip
    {
        private readonly Graph _graph;
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

        public static AudioClip CreateFromSource(AudioSource source, TimeSpan start, TimeSpan duration) => Transaction.Suppressed(() => new AudioClip(Graph.CreateAudioGraph(new AudioSourceNode { Source = source })) { Start = start, Duration = duration });

        public static AudioClip CreateTone(
            TimeSpan start, TimeSpan duration, Waveform waveform = Waveform.Sine,
            float frequencyHz = 440f, float amplitude = 1f) =>
            CreateFromSource(Transaction.Suppressed(() => new ToneAudioSource { Waveform = waveform, Frequency = new(frequencyHz), Amplitude = new(amplitude) }), start, duration);

        public static AudioClip CreateTimelineEmbed(Timeline timeline, TimeSpan start, TimeSpan duration) =>
            CreateFromSource(Transaction.Suppressed(() => new TimelineAudioSource { Timeline = timeline }), start, duration);

        /// <summary>Escape hatch for a fully custom graph — see VideoClip.CreateCustom's own remarks.</summary>
        public static AudioClip CreateCustom(Graph graph, TimeSpan start, TimeSpan duration)
        {
            if (graph.Domain != NodeDomain.Audio)
                throw new ArgumentException("AudioClip requires an Audio-domain Graph.", nameof(graph));

            return Transaction.Suppressed(() => new AudioClip(graph) { Start = start, Duration = duration });
        }

        public override AudioClip Duplicate() => Transaction.Suppressed(() => new AudioClip(Graph.Duplicate()) { Start = Start, Duration = Duration, Speed = Speed, PreservePitch = PreservePitch });
    }
}
