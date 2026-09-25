using System.Collections.Generic;
using System.Numerics;
using SkiaSharp;
using EditSharp.Components;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Nodes.Effects
{
    /// <summary>Draws a blurred, coloured copy of the image's shape behind it.</summary>
    /// <remarks>Inputs: Image, and an optional Mask; with a mask, the shadow applies only where the mask is. Output: Image.</remarks>
    [NodeKind("drop-shadow", DisplayName = "Drop shadow")]
    public sealed class DropShadowNode : Node
    {
        Animatable<Vector2> _offset = new(new(0.01f, 0.01f));
        /// <summary>Where the shadow sits relative to the image, in half-frames: X of half the frame's width, Y of half its height, y up.</summary>
        [Editable("Offset", Frame = FrameMeasure.HalfFrame)]
        public Animatable<Vector2> Offset { get => _offset; set => Transaction.Set(this, ref _offset, value, static (o, v) => o._offset = v); }
        Animatable<float> _blur = new(0.01f);
        /// <summary>How soft the shadow is (the blur's standard deviation), as a fraction of the frame's width.</summary>
        [Editable("Blur", Min = 0, Max = 0.5, Step = 0.001, Frame = FrameMeasure.Width)]
        public Animatable<float> Blur { get => _blur; set => Transaction.Set(this, ref _blur, value, static (o, v) => o._blur = v); }
        Animatable<SKColor> _color = new(SKColors.Black);
        /// <summary>The shadow's colour; its alpha is the shadow's opacity.</summary>
        [Editable("Color")]
        public Animatable<SKColor> Color { get => _color; set => Transaction.Set(this, ref _color, value, static (o, v) => o._color = v); }

        private static readonly NodePort[] StaticPorts =
        [
            new("Image", PortType.Image, PortDirection.Input),
            new("Mask", PortType.Mask, PortDirection.Input, optional: true),
            new("Image", PortType.Image, PortDirection.Output),
        ];

        /// <inheritdoc/>
        public override IReadOnlyList<NodePort> Ports => StaticPorts;

        /// <inheritdoc/>
        public override IEnumerable<IAnimatable> Animatables => [Offset, Blur, Color];

        /// <inheritdoc/>
        public override Node Duplicate() => Transaction.Suppressed(() => new DropShadowNode
        {
            Enabled = Enabled,
            Offset = Offset.Duplicate(),
            Blur = Blur.Duplicate(),
            Color = Color.Duplicate(),
        });
    }
}
