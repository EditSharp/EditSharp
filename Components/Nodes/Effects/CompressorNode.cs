using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Nodes;
 
namespace EditSharp.Components.Nodes.Effects
{
    public sealed class CompressorNode : Node
    {
        public Animatable<float> Threshold { get; set; } = new(-18f);
        public Animatable<float> Ratio { get; set; } = new(4f);
        public Animatable<float> AttackMs { get; set; } = new(10f);
        public Animatable<float> ReleaseMs { get; set; } = new(100f);
        public Animatable<float> MakeupGainDb { get; set; } = new(0f);
 
        private static readonly NodePort[] StaticPorts =
        [
            new("Audio", PortType.Audio, PortDirection.Input),
            new("Audio", PortType.Audio, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override Node Duplicate() => new CompressorNode
        {
            Enabled = Enabled,
            Threshold = Threshold.Duplicate(),
            Ratio = Ratio.Duplicate(),
            AttackMs = AttackMs.Duplicate(),
            ReleaseMs = ReleaseMs.Duplicate(),
            MakeupGainDb = MakeupGainDb.Duplicate(),
        };
    }
}
 