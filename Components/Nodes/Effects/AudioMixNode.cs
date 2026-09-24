using EditSharp.Audio.Processors;
using EditSharp.Audio.Engine;
using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;
 
namespace EditSharp.Components.Nodes.Effects
{
    /// <summary>
    /// The audio-domain equivalent of MergeNode — enables parallel
    /// processing (e.g. parallel compression: split the signal, compress
    /// one branch heavily, mix it back under the dry signal), and the
    /// canonical way to combine TWO InputNodes in one audio graph now that
    /// a graph can have more than one.
    /// </summary>
    public sealed class AudioMixNode : Node
    {
        Animatable<float> _mixA = new(1f);
        [Editable("Mix A", Min = 0, Max = 1, Step = 0.01)]
        public Animatable<float> MixA { get => _mixA; set => Transaction.Set(this, ref _mixA, value, static (o, v) => o._mixA = v); }
        Animatable<float> _mixB = new(1f);
        [Editable("Mix B", Min = 0, Max = 1, Step = 0.01)]
        public Animatable<float> MixB { get => _mixB; set => Transaction.Set(this, ref _mixB, value, static (o, v) => o._mixB = v); }
 
        private static readonly NodePort[] StaticPorts =
        [
            new("A", PortType.Audio, PortDirection.Input),
            new("B", PortType.Audio, PortDirection.Input),
 
            //optional Value modulation inputs (see
            //EditSharp.Components.Nodes.Math's own remarks) — when
            //connected, MULTIPLY against MixA/MixB's own keyframed values
            //rather than replacing them
            new("MixAModulation", PortType.Value, PortDirection.Input, optional: true),
            new("MixBModulation", PortType.Value, PortDirection.Input, optional: true),
 
            new("Result", PortType.Audio, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override IEnumerable<IAnimatable> Animatables => [MixA, MixB];
        internal override IAudioProcessor CreateAudioProcessor(AudioSession session) => new AudioMixProcessor(this);

        public override Node Duplicate() => Transaction.Suppressed(() => new AudioMixNode
        {
            Enabled = Enabled,
            MixA = MixA.Duplicate(),
            MixB = MixB.Duplicate(),
        });
    }
}
 