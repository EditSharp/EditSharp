using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Nodes;
 
namespace EditSharp.Components.Nodes.Effects
{
    public sealed class BlurNode : Node
    {
        public Animatable<float> Radius { get; set; } = new(0f);
 
        private static readonly NodePort[] StaticPorts =
        [
            new("Image", PortType.Image, PortDirection.Input),
            new("Mask", PortType.Mask, PortDirection.Input, optional: true),
            new("Image", PortType.Image, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override Node Duplicate() => new BlurNode { Enabled = Enabled, Radius = Radius.Duplicate() };
    }
}
 