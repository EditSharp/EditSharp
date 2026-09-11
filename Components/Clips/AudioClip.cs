using System;
using EditSharp.Components.Nodes;
using EditSharp.Components.Nodes.Sources;
using EditSharp.Components; // Source
 
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
 
        private AudioClip(Graph graph) => _graph = graph;
 
        public static AudioClip CreateFromSource(Source source, TimeSpan start, TimeSpan duration) =>
            new(Graph.CreateAudioGraph(new AudioSourceNode { Source = source })) { Start = start, Duration = duration };
 
        public static AudioClip CreateTone(
            TimeSpan start, TimeSpan duration, Waveform waveform = Waveform.Sine,
            float frequencyHz = 440f, float amplitude = 1f) =>
            new(Graph.CreateAudioGraph(new ToneGeneratorInputNode
            {
                Waveform = waveform,
                Frequency = new(frequencyHz),
                Amplitude = new(amplitude),
            }))
            { Start = start, Duration = duration };
 
        public static AudioClip CreateTimelineEmbed(TimelineReference reference, TimeSpan start, TimeSpan duration) =>
            new(Graph.CreateAudioGraph(new TimelineAudioInputNode { Reference = reference })) { Start = start, Duration = duration };
 
        /// <summary>Escape hatch for a fully custom graph — see VideoClip.CreateCustom's own remarks.</summary>
        public static AudioClip CreateCustom(Graph graph, TimeSpan start, TimeSpan duration)
        {
            if (graph.Domain != NodeDomain.Audio)
                throw new ArgumentException("AudioClip requires an Audio-domain Graph.", nameof(graph));
 
            return new AudioClip(graph) { Start = start, Duration = duration };
        }
 
        public override AudioClip Duplicate() => new(Graph.Duplicate()) { Start = Start, Duration = Duration };
    }
}
 