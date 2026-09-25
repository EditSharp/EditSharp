using System.Collections.Generic;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Nodes.Effects
{
    /// <summary>What an <see cref="ImageToMaskNode"/> makes its mask from.</summary>
    public enum MaskChannelSource
    {
        /// <summary>Brightness: white is fully inside the mask, black fully outside.</summary>
        Luma,

        /// <summary>Opacity: opaque is fully inside the mask, transparent fully outside.</summary>
        Alpha,
    }

    /// <summary>Makes a mask from an image's brightness or opacity.</summary>
    /// <remarks>Input: Image. Output: Mask.</remarks>
    public sealed class ImageToMaskNode : Node
    {
        MaskChannelSource _channel = MaskChannelSource.Alpha;
        /// <summary>Which part of the image becomes the mask.</summary>
        [Editable("Channel")]
        public MaskChannelSource Channel { get => _channel; set => Transaction.Set(this, ref _channel, value, static (o, v) => o._channel = v); }

        private static readonly NodePort[] StaticPorts =
        [
            new("Image", PortType.Image, PortDirection.Input),
            new("Mask", PortType.Mask, PortDirection.Output),
        ];

        /// <inheritdoc/>
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        /// <inheritdoc/>
        public override Node Duplicate() => Transaction.Suppressed(() => new ImageToMaskNode { Enabled = Enabled, Channel = Channel });
    }
}
