using System.Collections.Generic;
using SkiaSharp;
using EditSharp.Components;
using EditSharp.Components.Nodes;
 
namespace EditSharp.Components.Nodes.Effects
{
    /// <summary>
    /// Color tint, doubling as transparency via alpha. Auto-created and
    /// wired in by default (see Graph.CreateVideoGraph) alongside
    /// TransformNode — this is what replaced the old flat
    /// VisualClip.Modulate property entirely, fully consistent with how
    /// GainNode already replaced the old flat AudioClip.Volume: a normal,
    /// removable, reorderable node beyond the default, not a fixed clip
    /// property any more.
    /// </summary>
    public sealed class TintNode : Node
    {
        public Animatable<SKColor> Color { get; set; } = new(SKColors.White);
 
        private static readonly NodePort[] StaticPorts =
        [
            new("Image", PortType.Image, PortDirection.Input),
            new("Image", PortType.Image, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override Node Duplicate() => new TintNode { Enabled = Enabled, Color = Color.Duplicate() };
    }
}
 