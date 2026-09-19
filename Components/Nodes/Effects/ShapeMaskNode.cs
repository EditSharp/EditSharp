using System.Collections.Generic;
using System.Numerics;
using EditSharp.Components;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;
 
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
        ShapeType _shape = ShapeType.Rectangle;
        [Editable("Shape")]
        public ShapeType Shape { get => _shape; set => Transaction.Set(this, ref _shape, value, static (o, v) => o._shape = v); }
        Animatable<Vector2> _position = new(default);
        [Editable("Position")]
        public Animatable<Vector2> Position { get => _position; set => Transaction.Set(this, ref _position, value, static (o, v) => o._position = v); }
        Animatable<Vector2> _size = new(new Vector2(1, 1));
        [Editable("Size")]
        public Animatable<Vector2> Size { get => _size; set => Transaction.Set(this, ref _size, value, static (o, v) => o._size = v); }
        Animatable<float> _rotation = new(0f);
        [Editable("Rotation", Editor = PropertyEditor.Angle)]
        public Animatable<float> Rotation { get => _rotation; set => Transaction.Set(this, ref _rotation, value, static (o, v) => o._rotation = v); }
        Animatable<float> _feather = new(0f);
        [Editable("Feather", Min = 0, Max = 1, Step = 0.001)]
        public Animatable<float> Feather { get => _feather; set => Transaction.Set(this, ref _feather, value, static (o, v) => o._feather = v); }
 
        //only meaningful when Shape == Polygon. Kept as a plain list rather
        //than dedicated add/remove/reorder endpoints — flagged as an open
        //question in the schema doc, not settled here
        List<Animatable<Vector2>> _polygonPoints = [];
        [Editable("Points")]
        [VisibleWhen(nameof(Shape), ShapeType.Polygon)]
        public List<Animatable<Vector2>> PolygonPoints { get => _polygonPoints; set => Transaction.Set(this, ref _polygonPoints, value, static (o, v) => o._polygonPoints = v); }
 
        private static readonly NodePort[] StaticPorts = [new("Mask", PortType.Mask, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override IEnumerable<IAnimatable> Animatables => [Position, Size, Rotation, Feather, .. PolygonPoints];

        public override Node Duplicate() => Transaction.Suppressed(() => new ShapeMaskNode
        {
            Enabled = Enabled,
            Shape = Shape,
            Position = Position.Duplicate(),
            Size = Size.Duplicate(),
            Rotation = Rotation.Duplicate(),
            Feather = Feather.Duplicate(),
            PolygonPoints = [.. PolygonPoints.ConvertAll(p => p.Duplicate())],
        });
    }
}
 