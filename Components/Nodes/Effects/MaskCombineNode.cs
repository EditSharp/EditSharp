using System.Collections.Generic;
using EditSharp.Components.Nodes;
 
namespace EditSharp.Components.Nodes.Effects
{
    public enum MaskCombineMode { Add, Subtract, Intersect }
 
    public sealed class MaskCombineNode : Node
    {
        public MaskCombineMode Mode { get; set; } = MaskCombineMode.Add;
 
        private static readonly NodePort[] StaticPorts =
        [
            new("A", PortType.Mask, PortDirection.Input),
            new("B", PortType.Mask, PortDirection.Input),
            new("Result", PortType.Mask, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override Node Duplicate() => new MaskCombineNode { Enabled = Enabled, Mode = Mode };
    }
}
 