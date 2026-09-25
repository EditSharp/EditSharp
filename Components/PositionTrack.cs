using System;
using System.Numerics;
using EditSharp.History;

namespace EditSharp.Components
{
    /// <summary>A position keyframe, with handles that shape the path through it as well as the timing.</summary>
    public sealed class SpatialKeyframe : Keyframe<Vector2>
    {
        Vector2? _spatialInHandle;
        /// <summary>The path's control point arriving at this keyframe, as an offset from its position; null runs straight.</summary>
        public Vector2? SpatialInHandle { get => _spatialInHandle; internal set => Transaction.Set(this, ref _spatialInHandle, value, static (o, v) => o._spatialInHandle = v); }
        Vector2? _spatialOutHandle;
        /// <summary>The path's control point leaving this keyframe, as an offset from its position; null runs straight.</summary>
        public Vector2? SpatialOutHandle { get => _spatialOutHandle; internal set => Transaction.Set(this, ref _spatialOutHandle, value, static (o, v) => o._spatialOutHandle = v); }

        TangentMode _spatialInTangentMode = TangentMode.Auto;
        /// <summary>How the arriving path handle is set.</summary>
        public TangentMode SpatialInTangentMode { get => _spatialInTangentMode; internal set => Transaction.Set(this, ref _spatialInTangentMode, value, static (o, v) => o._spatialInTangentMode = v); }
        TangentMode _spatialOutTangentMode = TangentMode.Auto;
        /// <summary>How the leaving path handle is set.</summary>
        public TangentMode SpatialOutTangentMode { get => _spatialOutTangentMode; internal set => Transaction.Set(this, ref _spatialOutTangentMode, value, static (o, v) => o._spatialOutTangentMode = v); }

        internal SpatialKeyframe(TimeSpan start, Vector2 value) : base(start, value) { }
    }

    /// <summary>A position track whose keyframes shape the path between them as well as the timing.</summary>
    /// <remarks>
    /// A segment is evaluated in two steps. The time handles give how far along it
    /// the moment is, the same way every track does; that fraction is then the point
    /// along the path the spatial handles shape. Easing each axis on its own would
    /// bend a straight path.
    /// </remarks>
    public sealed class PositionTrack : KeyframeTrack<Vector2>
    {
        /// <inheritdoc/>
        protected override Keyframe<Vector2> CreateKeyframe(TimeSpan start, Vector2 value) =>
            new SpatialKeyframe(start, value);

        /// <summary>Adds a keyframe, or updates the value of the one already at that time.</summary>
        /// <param name="start">When, in content time.</param>
        /// <param name="value">The position.</param>
        /// <returns>The keyframe.</returns>
        public SpatialKeyframe AddSpatialKeyframe(TimeSpan start, Vector2 value) =>
            (SpatialKeyframe)AddKeyframe(start, value);

        /// <inheritdoc/>
        protected override KeyframeTrack<Vector2> CreateEmptyCopy() => new PositionTrack();

        /// <inheritdoc/>
        protected override void CopyKeyframeExtras(Keyframe<Vector2> source, Keyframe<Vector2> target)
        {
            if (source is not SpatialKeyframe from || target is not SpatialKeyframe to) return;

            to.SpatialInHandle = from.SpatialInHandle;
            to.SpatialOutHandle = from.SpatialOutHandle;
            to.SpatialInTangentMode = from.SpatialInTangentMode;
            to.SpatialOutTangentMode = from.SpatialOutTangentMode;
        }

        /// <summary>Sets one of a keyframe's path handles.</summary>
        /// <remarks>An Auto side becomes Free. A Smooth side mirrors the other handle through the keyframe.</remarks>
        /// <param name="keyframe">The keyframe.</param>
        /// <param name="isInHandle">True for the arriving handle, false for the leaving one.</param>
        /// <param name="offset">The control point, as an offset from the keyframe's position.</param>
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

        /// <inheritdoc/>
        protected override Vector2 InterpolateSegment(Keyframe<Vector2> from, Keyframe<Vector2> to, TimeSpan time)
        {
            if (from.OutInterpolation == InterpolationType.Hold) return from.Value;

            //how far along the segment, from the time handles
            float u = SolveU(time, from, to);

            //that far along the path the spatial handles shape
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
