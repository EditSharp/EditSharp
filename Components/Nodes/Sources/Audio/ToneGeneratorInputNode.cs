using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Nodes;
 
namespace EditSharp.Components.Nodes.Sources.Audio
{
    public enum Waveform { Sine, Square, Sawtooth, Triangle }
 
    /// <summary>
    /// A synthesized tone — NEW node type, the audio-domain equivalent of
    /// ColorGeneratorInputNode on the video side (there was no prior
    /// "AudioGeneratorClip"; a "// future:" comment was the only trace of
    /// this idea before this rewrite). No in-point concept — procedural,
    /// generates for however long it's asked, same as NoiseInputNode.
    /// </summary>
    public sealed class ToneGeneratorInputNode : InputNode
    {
        public Waveform Waveform { get; set; } = Waveform.Sine;
        public Animatable<float> Frequency { get; set; } = new(440f);
        public Animatable<float> Amplitude { get; set; } = new(1f);
 
        private static readonly NodePort[] StaticPorts = [new("Audio", PortType.Audio, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override Node Duplicate() => new ToneGeneratorInputNode
        {
            Enabled = Enabled,
            Waveform = Waveform,
            Frequency = Frequency.Duplicate(),
            Amplitude = Amplitude.Duplicate(),
        };
    }
}
 