using System.Collections.Generic;
using EditSharp.Components.Nodes;
 
namespace EditSharp.Components.Nodes.Effects
{
    public enum MaskChannelSource { Luma, Alpha }
 
    public sealed class ImageToMaskNode : Node
    {
        public MaskChannelSource Channel { get; set; } = MaskChannelSource.Alpha;
 
        private static readonly NodePort[] StaticPorts =
        [
            new("Image", PortType.Image, PortDirection.Input),
            new("Mask", PortType.Mask, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override Node Duplicate() => new ImageToMaskNode { Enabled = Enabled, Channel = Channel };
    }
}
 