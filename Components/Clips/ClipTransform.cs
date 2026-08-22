using System;
using System.Collections.Generic;
using System.Numerics;
using EditSharp.Components;
 
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
        public Animatable<Vector2> Position { get; set; } = new(default);
        public Animatable<Vector2> Scale { get; set; } = new(new Vector2(1, 1));
        public Animatable<float> Rotation { get; set; } = new(0f);
        public Animatable<float> Pitch { get; set; } = new(0f);
        public Animatable<float> Yaw { get; set; } = new(0f);
 
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
 
        public ClipTransform Duplicate() => new()
        {
            Position = Position.Duplicate(),
            Scale = Scale.Duplicate(),
            Rotation = Rotation.Duplicate(),
            Pitch = Pitch.Duplicate(),
            Yaw = Yaw.Duplicate(),
        };
 
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
 