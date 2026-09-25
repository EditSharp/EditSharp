using System.Collections.Generic;
using SkiaSharp;
using EditSharp.Components;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Nodes.Effects
{
    /// <summary>Multiplies the image by a colour; the colour's alpha sets the image's opacity.</summary>
    /// <remarks>Input: Image. Output: Image. A new video clip's graph has one.</remarks>
    [NodeKind("tint", DisplayName = "Tint")]
    public sealed class TintNode : Node
    {
        Animatable<SKColor> _color = new(SKColors.White);
        /// <summary>The colour to multiply by; white leaves the image unchanged.</summary>
        [Editable("Color")]
        public Animatable<SKColor> Color { get => _color; set => Transaction.Set(this, ref _color, value, static (o, v) => o._color = v); }

        private static readonly NodePort[] StaticPorts =
        [
            new("Image", PortType.Image, PortDirection.Input),
            new("Image", PortType.Image, PortDirection.Output),
        ];

        /// <inheritdoc/>
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        /// <inheritdoc/>
        public override IEnumerable<IAnimatable> Animatables => [Color];

        /// <inheritdoc/>
        public override Node Duplicate() => Transaction.Suppressed(() => new TintNode { Enabled = Enabled, Color = Color.Duplicate() });
    }
}
