using System.Collections.Generic;
using System.Numerics;
using SkiaSharp;
using EditSharp.Components;
using EditSharp.Components.Nodes;
 
namespace EditSharp.Components.Nodes.Effects
{
    public sealed class DropShadowNode : Node
    {
        public Animatable<Vector2> Offset { get; set; } = new(default);
        public Animatable<float> Blur { get; set; } = new(0f);
        public Animatable<SKColor> Color { get; set; } = new(SKColors.Black);
 
        private static readonly NodePort[] StaticPorts =
        [
            new("Image", PortType.Image, PortDirection.Input),
            new("Mask", PortType.Mask, PortDirection.Input, optional: true),
            new("Image", PortType.Image, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override Node Duplicate() => new DropShadowNode
        {
            Enabled = Enabled,
            Offset = Offset.Duplicate(),
            Blur = Blur.Duplicate(),
            Color = Color.Duplicate(),
        };
    }
}
 