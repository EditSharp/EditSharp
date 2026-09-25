using System.Collections.Generic;
using SkiaSharp;
using EditSharp.Components;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;

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
        Animatable<SKColor> _color = new(SKColors.White);
        [Editable("Color")]
        public Animatable<SKColor> Color { get => _color; set => Transaction.Set(this, ref _color, value, static (o, v) => o._color = v); }

        private static readonly NodePort[] StaticPorts =
        [
            new("Image", PortType.Image, PortDirection.Input),
            new("Image", PortType.Image, PortDirection.Output),
        ];

        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override IEnumerable<IAnimatable> Animatables => [Color];

        public override Node Duplicate() => Transaction.Suppressed(() => new TintNode { Enabled = Enabled, Color = Color.Duplicate() });
    }
}
