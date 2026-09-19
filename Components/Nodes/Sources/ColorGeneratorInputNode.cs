using System.Collections.Generic;
using SkiaSharp;
using EditSharp.Components;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;
 
namespace EditSharp.Components.Nodes.Sources
{
    /// <summary>
    /// Replaces the old GeneratorClip. The old ColorIn/ColorOut/ColorMain
    /// discrete fade-tuple fields are gone in favor of an ordinary
    /// keyframeable Color — the same simplification this schema already
    /// applied elsewhere (flat Volume -> GainNode, flat ClipTransform ->
    /// per-field Animatable): a fade-in/hold/fade-out is just three
    /// keyframes on one Animatable&lt;SKColor&gt; now, rather than a
    /// bespoke shape only this one node type understood.
    /// </summary>
    public sealed class ColorGeneratorInputNode : InputNode
    {
        Animatable<SKColor> _color = new(SKColors.Black);
        [Editable("Color")]
        public Animatable<SKColor> Color { get => _color; set => Transaction.Set(this, ref _color, value, static (o, v) => o._color = v); }
 
        private static readonly NodePort[] StaticPorts = [new("Image", PortType.Image, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override IEnumerable<IAnimatable> Animatables => [Color];

        public override Node Duplicate() => Transaction.Suppressed(() => new ColorGeneratorInputNode { Enabled = Enabled, Color = Color.Duplicate() });
    }
}
 