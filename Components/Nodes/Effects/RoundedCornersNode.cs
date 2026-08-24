using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Nodes;
 
namespace EditSharp.Components.Nodes.Effects
{
    public sealed class RoundedCornersNode : Node
    {
        public Animatable<float> Radius { get; set; } = new(0.1f);
 
        private static readonly NodePort[] StaticPorts =
        [
            new("Image", PortType.Image, PortDirection.Input),
            new("Mask", PortType.Mask, PortDirection.Input, optional: true),
            new("Image", PortType.Image, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override Node Duplicate() => new RoundedCornersNode { Enabled = Enabled, Radius = Radius.Duplicate() };
    }
}
 