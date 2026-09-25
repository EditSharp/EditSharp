using System.Collections.Generic;
using System.Numerics;
using EditSharp.Components;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Nodes.Effects
{
    /// <summary>The shape a <see cref="ShapeMaskNode"/> draws.</summary>
    public enum ShapeType
    {
        /// <summary>A rectangle filling the node's Size.</summary>
        Rectangle,

        /// <summary>An ellipse filling the node's Size.</summary>
        Ellipse,

        /// <summary>The shape outlined by the node's PolygonPoints.</summary>
        Polygon,
    }

    /// <summary>A mask in the shape of a rectangle, an ellipse or a polygon.</summary>
    /// <remarks>Output: Mask. It's drawn at frame size and scaled to the image it masks, so its geometry is measured against the frame.</remarks>
    public sealed class ShapeMaskNode : Node
    {
        ShapeType _shape = ShapeType.Rectangle;
        /// <summary>Which shape to draw.</summary>
        [Editable("Shape")]
        public ShapeType Shape { get => _shape; set => Transaction.Set(this, ref _shape, value, static (o, v) => o._shape = v); }
        Animatable<Vector2> _position = new(default);
        /// <summary>The shape's centre, in half-frames from the frame's centre: X of half the frame's width, Y of half its height, y up.</summary>
        [Editable("Position", Frame = FrameMeasure.HalfFrame)]
        public Animatable<Vector2> Position { get => _position; set => Transaction.Set(this, ref _position, value, static (o, v) => o._position = v); }
        Animatable<Vector2> _size = new(new Vector2(1, 1));
        /// <summary>The size of a rectangle or ellipse: X as a fraction of the frame's width, Y of its height.</summary>
        [Editable("Size", Frame = FrameMeasure.Frame)]
        public Animatable<Vector2> Size { get => _size; set => Transaction.Set(this, ref _size, value, static (o, v) => o._size = v); }
        Animatable<float> _rotation = new(0f);
        /// <summary>The shape's rotation about its centre, in degrees clockwise.</summary>
        [Editable("Rotation", Editor = PropertyEditor.Angle)]
        public Animatable<float> Rotation { get => _rotation; set => Transaction.Set(this, ref _rotation, value, static (o, v) => o._rotation = v); }
        Animatable<float> _feather = new(0f);
        /// <summary>How soft the edge is (a blur's standard deviation), as a fraction of the frame's shorter side.</summary>
        [Editable("Feather", Min = 0, Max = 1, Step = 0.001)]
        public Animatable<float> Feather { get => _feather; set => Transaction.Set(this, ref _feather, value, static (o, v) => o._feather = v); }

        List<Animatable<Vector2>> _polygonPoints = [];
        /// <summary>A polygon's corners, in half-frames from <see cref="Position"/>, turned with <see cref="Rotation"/>.</summary>
        /// <remarks>Used only when <see cref="Shape"/> is Polygon; fewer than three points draws the rectangle instead.</remarks>
        [Editable("Points", Frame = FrameMeasure.HalfFrame)]
        [VisibleWhen(nameof(Shape), ShapeType.Polygon)]
        public List<Animatable<Vector2>> PolygonPoints { get => _polygonPoints; set => Transaction.Set(this, ref _polygonPoints, value, static (o, v) => o._polygonPoints = v); }

        private static readonly NodePort[] StaticPorts = [new("Mask", PortType.Mask, PortDirection.Output)];
        /// <inheritdoc/>
        public override IReadOnlyList<NodePort> Ports => StaticPorts;

        /// <inheritdoc/>
        public override IEnumerable<IAnimatable> Animatables => [Position, Size, Rotation, Feather, .. PolygonPoints];

        /// <inheritdoc/>
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
