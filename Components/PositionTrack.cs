using System;
using System.Numerics;
 
namespace EditSharp.Components
{
    /// <summary>
    /// A position keyframe adds a SECOND, independent kind of handle beyond
    /// the temporal one every Keyframe&lt;T&gt; already has: the shape of the
    /// motion path itself in canvas space, separate from how speed eases
    /// along that path over time. See the schema doc's "Spatial handles for
    /// position" section for the full AE/Resolve-style reasoning.
    /// </summary>
    public sealed class SpatialKeyframe : Keyframe<Vector2>
    {
        //offsets in CANVAS space, not time — shape of the path arriving/leaving
        public Vector2? SpatialInHandle { get; internal set; }
        public Vector2? SpatialOutHandle { get; internal set; }
 
        public TangentMode SpatialInTangentMode { get; internal set; } = TangentMode.Auto;
        public TangentMode SpatialOutTangentMode { get; internal set; } = TangentMode.Auto;
 
        internal SpatialKeyframe(TimeSpan start, Vector2 value) : base(start, value) { }
    }
 
    /// <summary>
    /// Position's own KeyframeTrack&lt;Vector2&gt; subclass. Evaluate is
    /// deliberately two-stage rather than sharing the base class's plain
    /// value-cubic:
    ///   1. Temporal stage: the ordinary Hold/Linear/Bezier handle logic
    ///      (this class's inherited InHandle/OutHandle, via the base's
    ///      shared SolveU) produces a scalar progress u in [0,1] between the
    ///      two bounding keyframes — exactly the same computation every
    ///      other KeyframeTrack&lt;T&gt; does to find its cubic parameter.
    ///   2. Spatial stage: u is fed as the parametric input to the cubic
    ///      bezier PATH defined by (left.Value, left.SpatialOutHandle,
    ///      right.SpatialInHandle, right.Value).
    ///
    /// Easing each axis independently (the base class's own approach, if it
    /// were used unmodified here) is the classic bug that produces visibly
    /// curved motion on an intended straight line — see the schema doc for
    /// why "how fast" and "which way" have to be two curves composed through
    /// one shared progress value instead.
    /// </summary>
    public sealed class PositionTrack : KeyframeTrack<Vector2>
    {
        protected override Keyframe<Vector2> CreateKeyframe(TimeSpan start, Vector2 value) =>
            new SpatialKeyframe(start, value);
 
        public SpatialKeyframe AddSpatialKeyframe(TimeSpan start, Vector2 value) =>
            (SpatialKeyframe)AddKeyframe(start, value);
 
        public void SetSpatialHandle(SpatialKeyframe keyframe, bool isInHandle, Vector2 offset)
        {
            if (isInHandle)
            {
                keyframe.SpatialInHandle = offset;
                if (keyframe.SpatialInTangentMode == TangentMode.Auto)
                    keyframe.SpatialInTangentMode = TangentMode.Free;
                if (keyframe.SpatialInTangentMode == TangentMode.Smooth)
                    keyframe.SpatialOutHandle = -offset;
            }
            else
            {
                keyframe.SpatialOutHandle = offset;
                if (keyframe.SpatialOutTangentMode == TangentMode.Auto)
                    keyframe.SpatialOutTangentMode = TangentMode.Free;
                if (keyframe.SpatialOutTangentMode == TangentMode.Smooth)
                    keyframe.SpatialInHandle = -offset;
            }
        }
 
        protected override Vector2 InterpolateSegment(Keyframe<Vector2> from, Keyframe<Vector2> to, TimeSpan time)
        {
            if (from.OutInterpolation == InterpolationType.Hold) return from.Value;
 
            //stage 1: temporal — reuses the exact same Hold/Linear/Bezier
            //cubic-solve every other track uses, just to get u rather than a
            //final value
            float u = SolveU(time, from, to);
 
            //stage 2: spatial — a DIFFERENT cubic, over the path shape
            var fromSpatial = (SpatialKeyframe)from;
            var toSpatial = (SpatialKeyframe)to;
 
            Vector2 p1 = fromSpatial.SpatialOutHandle.HasValue
                ? fromSpatial.Value + fromSpatial.SpatialOutHandle.Value
                : fromSpatial.Value;
 
            Vector2 p2 = toSpatial.SpatialInHandle.HasValue
                ? toSpatial.Value + toSpatial.SpatialInHandle.Value
                : toSpatial.Value;
 
            float m = 1 - u;
 
            return (m * m * m * fromSpatial.Value)
                 + (3 * m * m * u * p1)
                 + (3 * m * u * u * p2)
                 + (u * u * u * toSpatial.Value);
        }
    }
}
 