using System.Collections.Generic;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Nodes.Effects
{
    public enum MaskChannelSource { Luma, Alpha }

    public sealed class ImageToMaskNode : Node
    {
        MaskChannelSource _channel = MaskChannelSource.Alpha;
        [Editable("Channel")]
        public MaskChannelSource Channel { get => _channel; set => Transaction.Set(this, ref _channel, value, static (o, v) => o._channel = v); }

        private static readonly NodePort[] StaticPorts =
        [
            new("Image", PortType.Image, PortDirection.Input),
            new("Mask", PortType.Mask, PortDirection.Output),
        ];

        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override Node Duplicate() => Transaction.Suppressed(() => new ImageToMaskNode { Enabled = Enabled, Channel = Channel });
    }
}
