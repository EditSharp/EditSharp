using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Nodes;
 
namespace EditSharp.Components.Nodes.Effects
{
    public sealed class EQBand
    {
        public Animatable<float> FrequencyHz { get; set; } = new(1000f);
        public Animatable<float> GainDb { get; set; } = new(0f);
        public Animatable<float> Q { get; set; } = new(1f);
 
        public EQBand Duplicate() => new()
        {
            FrequencyHz = FrequencyHz.Duplicate(),
            GainDb = GainDb.Duplicate(),
            Q = Q.Duplicate(),
        };
    }
 
    public sealed class EQNode : Node
    {
        public List<EQBand> Bands { get; set; } = [];
 
        private static readonly NodePort[] StaticPorts =
        [
            new("Audio", PortType.Audio, PortDirection.Input),
            new("Audio", PortType.Audio, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override Node Duplicate() => new EQNode
        {
            Enabled = Enabled,
            Bands = [.. Bands.ConvertAll(b => b.Duplicate())],
        };
    }
}
 