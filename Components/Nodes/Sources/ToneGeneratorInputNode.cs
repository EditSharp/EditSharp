using EditSharp.Audio.Processors;
using EditSharp.Audio.Engine;
using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;
 
namespace EditSharp.Components.Nodes.Sources
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
        Waveform _waveform = Waveform.Sine;
        [Editable("Waveform")]
        public Waveform Waveform { get => _waveform; set => Transaction.Set(this, ref _waveform, value, static (o, v) => o._waveform = v); }
        Animatable<float> _frequency = new(440f);
        [Editable("Frequency", Min = 20, Max = 20000, Step = 1, Unit = "Hz")]
        public Animatable<float> Frequency { get => _frequency; set => Transaction.Set(this, ref _frequency, value, static (o, v) => o._frequency = v); }
        Animatable<float> _amplitude = new(1f);
        [Editable("Amplitude", Min = 0, Max = 1, Step = 0.01)]
        public Animatable<float> Amplitude { get => _amplitude; set => Transaction.Set(this, ref _amplitude, value, static (o, v) => o._amplitude = v); }
 
        private static readonly NodePort[] StaticPorts = [new("Audio", PortType.Audio, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override IEnumerable<IAnimatable> Animatables => [Frequency, Amplitude];
        internal override IAudioProcessor CreateAudioProcessor(AudioSession session) =>
            new ContentInputProcessor(() => this, () => new ToneContentAudio(this, session.Format), session);

        public override Node Duplicate() => Transaction.Suppressed(() => new ToneGeneratorInputNode
        {
            Enabled = Enabled,
            Waveform = Waveform,
            Frequency = Frequency.Duplicate(),
            Amplitude = Amplitude.Duplicate(),
        });
    }
}
 