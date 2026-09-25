using System;
using System.Collections.Generic;
using System.Numerics;
using EditSharp.Components;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Clips
{
    /// <summary>A <see cref="ClipTransform"/>'s values at one moment.</summary>
    /// <param name="Position">Where the content's centre is, in half-frames from the frame's centre, y up.</param>
    /// <param name="Scale">The content's size as a multiple of its fit inside the frame, per axis.</param>
    /// <param name="Rotation">The turn in the frame's plane, in degrees clockwise.</param>
    /// <param name="Pitch">The tilt about the horizontal axis, in degrees.</param>
    /// <param name="Yaw">The turn about the vertical axis, in degrees.</param>
    public readonly record struct ResolvedTransform(Vector2 Position, Vector2 Scale, float Rotation, float Pitch, float Yaw);

    /// <summary>Where an image sits in the frame: its position, size and rotation, each keyframeable on its own.</summary>
    /// <remarks>At the defaults the content fits inside the frame, centred. Rotations apply yaw, then pitch, then rotation, seen in perspective with a 90 degree horizontal field of view.</remarks>
    public sealed class ClipTransform
    {
        Animatable<Vector2> _position = new(default);
        /// <summary>Where the content's centre is, in half-frames from the frame's centre: X of half the frame's width, Y of half its height, y up.</summary>
        [Editable("Position", Frame = FrameMeasure.HalfFrame)]
        public Animatable<Vector2> Position { get => _position; set => Transaction.Set(this, ref _position, value, static (o, v) => o._position = v); }
        Animatable<Vector2> _scale = new(new Vector2(1, 1));
        /// <summary>The content's size as a multiple of its fit inside the frame, per axis; (1, 1) fits it.</summary>
        [Editable("Scale")]
        public Animatable<Vector2> Scale { get => _scale; set => Transaction.Set(this, ref _scale, value, static (o, v) => o._scale = v); }
        Animatable<float> _rotation = new(0f);
        /// <summary>The turn in the frame's plane, in degrees clockwise.</summary>
        [Editable("Rotation", Editor = PropertyEditor.Angle)]
        public Animatable<float> Rotation { get => _rotation; set => Transaction.Set(this, ref _rotation, value, static (o, v) => o._rotation = v); }
        Animatable<float> _pitch = new(0f);
        /// <summary>The tilt about the horizontal axis, in degrees.</summary>
        [Editable("Pitch", Editor = PropertyEditor.Angle)]
        public Animatable<float> Pitch { get => _pitch; set => Transaction.Set(this, ref _pitch, value, static (o, v) => o._pitch = v); }
        Animatable<float> _yaw = new(0f);
        /// <summary>The turn about the vertical axis, in degrees.</summary>
        [Editable("Yaw", Editor = PropertyEditor.Angle)]
        public Animatable<float> Yaw { get => _yaw; set => Transaction.Set(this, ref _yaw, value, static (o, v) => o._yaw = v); }

        /// <summary>Every keyframeable value on the transform.</summary>
        public IEnumerable<IAnimatable> Animatables => [Position, Scale, Rotation, Pitch, Yaw];

        /// <summary>Gives <see cref="Position"/> a <see cref="Components.PositionTrack"/>, whose keyframes also shape the path between them.</summary>
        /// <returns>The track; the existing one if Position already has a PositionTrack.</returns>
        public PositionTrack UsePositionTrack()
        {
            if (Position.Track is PositionTrack existing) return existing;

            var track = new PositionTrack();
            Position.AttachTrack(track);
            return track;
        }

        /// <summary>A deep copy, keyframes included.</summary>
        /// <remarks>Nothing is recorded in history.</remarks>
        /// <returns>The copy.</returns>
        public ClipTransform Duplicate() => Transaction.Suppressed(() => new ClipTransform
        {
            Position = Position.Duplicate(),
            Scale = Scale.Duplicate(),
            Rotation = Rotation.Duplicate(),
            Pitch = Pitch.Duplicate(),
            Yaw = Yaw.Duplicate(),
        });

        /// <summary>Every value at one moment.</summary>
        /// <param name="clipRelativeTime">Content time: since the clip's in-point, at 1x.</param>
        /// <returns>The values.</returns>
        public ResolvedTransform Evaluate(TimeSpan clipRelativeTime) => new(
            Position.Evaluate(clipRelativeTime),
            Scale.Evaluate(clipRelativeTime),
            Rotation.Evaluate(clipRelativeTime),
            Pitch.Evaluate(clipRelativeTime),
            Yaw.Evaluate(clipRelativeTime));

        /// <summary>The <see cref="Position"/> value that puts the content's centre at one of nine points in the frame.</summary>
        /// <param name="position">The point.</param>
        /// <returns>The position, in half-frames: the corners are (±1, ±1).</returns>
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

    /// <summary>Nine points in the frame, for <see cref="ClipTransform.FromPosition"/>.</summary>
    public enum Positions
    {
        /// <summary>The top-left corner.</summary>
        TopLeft,

        /// <summary>The middle of the top edge.</summary>
        TopCenter,

        /// <summary>The top-right corner.</summary>
        TopRight,

        /// <summary>The middle of the left edge.</summary>
        CenterLeft,

        /// <summary>The centre of the frame.</summary>
        CenterMiddle,

        /// <summary>The middle of the right edge.</summary>
        CenterRight,

        /// <summary>The bottom-left corner.</summary>
        BottomLeft,

        /// <summary>The middle of the bottom edge.</summary>
        BottomCenter,

        /// <summary>The bottom-right corner.</summary>
        BottomRight,
    }
}
