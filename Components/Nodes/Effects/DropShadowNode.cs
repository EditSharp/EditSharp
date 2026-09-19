using System.Collections.Generic;
using System.Numerics;
using SkiaSharp;
using EditSharp.Components;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;
 
namespace EditSharp.Components.Nodes.Effects
{
    public sealed class DropShadowNode : Node
    {
        Animatable<Vector2> _offset = new(new(0.01f, 0.01f));
        [Editable("Offset")]
        public Animatable<Vector2> Offset { get => _offset; set => Transaction.Set(this, ref _offset, value, static (o, v) => o._offset = v); }
        Animatable<float> _blur = new(0.01f);
        [Editable("Blur", Min = 0, Max = 0.5, Step = 0.001)]
        public Animatable<float> Blur { get => _blur; set => Transaction.Set(this, ref _blur, value, static (o, v) => o._blur = v); }
        Animatable<SKColor> _color = new(SKColors.Black);
        [Editable("Color")]
        public Animatable<SKColor> Color { get => _color; set => Transaction.Set(this, ref _color, value, static (o, v) => o._color = v); }
 
        private static readonly NodePort[] StaticPorts =
        [
            new("Image", PortType.Image, PortDirection.Input),
            new("Mask", PortType.Mask, PortDirection.Input, optional: true),
            new("Image", PortType.Image, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override IEnumerable<IAnimatable> Animatables => [Offset, Blur, Color];

        public override Node Duplicate() => Transaction.Suppressed(() => new DropShadowNode
        {
            Enabled = Enabled,
            Offset = Offset.Duplicate(),
            Blur = Blur.Duplicate(),
            Color = Color.Duplicate(),
        });
    }
}
 