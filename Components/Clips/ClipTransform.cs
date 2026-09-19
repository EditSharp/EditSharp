using System;
using System.Collections.Generic;
using System.Numerics;
using EditSharp.Components;
using EditSharp.History;
using EditSharp.Editing;
 
namespace EditSharp.Components.Clips
{
    /// <summary>
    /// A resolved, plain-value snapshot of a ClipTransform at one instant —
    /// no tracks, just concrete numbers. This is what render code actually
    /// consumes; it replaces the old Clip.TransformAt(time)'s role, now
    /// composed from each field's own independently-timed Animatable&lt;T&gt;
    /// track instead of interpolating between two flat ClipTransform blobs.
    /// </summary>
    public readonly record struct ResolvedTransform(Vector2 Position, Vector2 Scale, float Rotation, float Pitch, float Yaw);
 
    /// <summary>
    /// Every field is individually Animatable&lt;T&gt; — see the schema doc's
    /// Keyframes section. This replaces the old model where an entire
    /// ClipTransform snapshot was one keyframe; here, Position can animate on
    /// a completely different schedule than Scale.
    /// </summary>
    public sealed class ClipTransform
    {
        Animatable<Vector2> _position = new(default);
        [Editable("Position")]
        public Animatable<Vector2> Position { get => _position; set => Transaction.Set(this, ref _position, value, static (o, v) => o._position = v); }
        Animatable<Vector2> _scale = new(new Vector2(1, 1));
        [Editable("Scale")]
        public Animatable<Vector2> Scale { get => _scale; set => Transaction.Set(this, ref _scale, value, static (o, v) => o._scale = v); }
        Animatable<float> _rotation = new(0f);
        [Editable("Rotation", Editor = PropertyEditor.Angle)]
        public Animatable<float> Rotation { get => _rotation; set => Transaction.Set(this, ref _rotation, value, static (o, v) => o._rotation = v); }
        Animatable<float> _pitch = new(0f);
        [Editable("Pitch", Editor = PropertyEditor.Angle)]
        public Animatable<float> Pitch { get => _pitch; set => Transaction.Set(this, ref _pitch, value, static (o, v) => o._pitch = v); }
        Animatable<float> _yaw = new(0f);
        [Editable("Yaw", Editor = PropertyEditor.Angle)]
        public Animatable<float> Yaw { get => _yaw; set => Transaction.Set(this, ref _yaw, value, static (o, v) => o._yaw = v); }

        /// <summary>Every track on this transform — see Node.Animatables.</summary>
        public IEnumerable<IAnimatable> Animatables => [Position, Scale, Rotation, Pitch, Yaw];
 
        /// <summary>
        /// Position specifically gets the spatial-handle PositionTrack
        /// treatment (see the schema doc) — this is a convenience that
        /// installs one and points Position.Track at it, rather than a
        /// second, separate field. Calling it more than once is a no-op if
        /// Position already has a track.
        /// </summary>
        public PositionTrack UsePositionTrack()
        {
            if (Position.Track is PositionTrack existing) return existing;
 
            var track = new PositionTrack();
            Position.AttachTrack(track);
            return track;
        }
 
        public ClipTransform Duplicate() => Transaction.Suppressed(() => new ClipTransform
        {
            Position = Position.Duplicate(),
            Scale = Scale.Duplicate(),
            Rotation = Rotation.Duplicate(),
            Pitch = Pitch.Duplicate(),
            Yaw = Yaw.Duplicate(),
        });
 
        public ResolvedTransform Evaluate(TimeSpan clipRelativeTime) => new(
            Position.Evaluate(clipRelativeTime),
            Scale.Evaluate(clipRelativeTime),
            Rotation.Evaluate(clipRelativeTime),
            Pitch.Evaluate(clipRelativeTime),
            Yaw.Evaluate(clipRelativeTime));
 
        public static Vector2 FromPosition(Positions position) => PositionToVector2[position];
 
        private static readonly Dictionary<Positions, Vector2> PositionToVector2 = new()
        {
            [Positions.TopLeft] = new Vector2(-1, 1),
            [Positions.TopCenter] = new Vector2(0, 1),
            [Positions.TopRight] = new Vector2(1, 1),
            [Positions.CenterLeft] = new Vector2(-1, 0),
            [Positions.CenterMiddle] = new Vector2(0, 0),
            [Positions.CenterRight] = new Vector2(1, 0),
            [Positions.BottomLeft] = new Vector2(-1, -1),
            [Positions.BottomCenter] = new Vector2(0, -1),
            [Positions.BottomRight] = new Vector2(1, -1),
        };
    }
 
    public enum Positions
    {
        TopLeft, TopCenter, TopRight,
        CenterLeft, CenterMiddle, CenterRight,
        BottomLeft, BottomCenter, BottomRight,
    }
}
 