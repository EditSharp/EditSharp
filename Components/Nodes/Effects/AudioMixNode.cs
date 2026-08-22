using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Nodes;
 
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
        public Animatable<float> MixA { get; set; } = new(1f);
        public Animatable<float> MixB { get; set; } = new(1f);
 
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
 
        public override Node Duplicate() => new AudioMixNode
        {
            Enabled = Enabled,
            MixA = MixA.Duplicate(),
            MixB = MixB.Duplicate(),
        };
    }
}
 