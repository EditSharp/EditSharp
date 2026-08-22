using System.Collections.Generic;
using System.Numerics;
using EditSharp.Components;
using EditSharp.Components.Nodes;
 
namespace EditSharp.Components.Nodes.Effects
{
    public enum ShapeType { Rectangle, Ellipse, Polygon }
 
    /// <summary>
    /// Geometry is normalized 0-1 — a fraction of whatever image this mask
    /// ends up merged against — rather than absolute pixels. See the schema
    /// doc's "Masks are canvas-agnostic" note for why this sidesteps the
    /// pre-/post-TransformNode coordinate-space question entirely.
    /// </summary>
    public sealed class ShapeMaskNode : Node
    {
        public ShapeType Shape { get; set; } = ShapeType.Rectangle;
        public Animatable<Vector2> Position { get; set; } = new(default);
        public Animatable<Vector2> Size { get; set; } = new(new Vector2(1, 1));
        public Animatable<float> Rotation { get; set; } = new(0f);
        public Animatable<float> Feather { get; set; } = new(0f);
 
        //only meaningful when Shape == Polygon. Kept as a plain list rather
        //than dedicated add/remove/reorder endpoints — flagged as an open
        //question in the schema doc, not settled here
        public List<Animatable<Vector2>> PolygonPoints { get; set; } = [];
 
        private static readonly NodePort[] StaticPorts = [new("Mask", PortType.Mask, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override Node Duplicate() => new ShapeMaskNode
        {
            Enabled = Enabled,
            Shape = Shape,
            Position = Position.Duplicate(),
            Size = Size.Duplicate(),
            Rotation = Rotation.Duplicate(),
            Feather = Feather.Duplicate(),
            PolygonPoints = [.. PolygonPoints.ConvertAll(p => p.Duplicate())],
        };
    }
}
 