using EditSharp.Components.Clips;
using System;

namespace EditSharp.Components
{
    public class Keyframe
    {
        //state of clip at keyframe position
        public required ClipTransform Transform { get; set; }

        //time after beginning of parent clip where keyframe should be placed
        public required TimeSpan Start { get; set; }

        //motion type to use into this keyframe
        public Interpolation InterpolationIn { get; set; } = Interpolation.Linear;

        //motion type to use out of this keyframe
        public Interpolation InterpolationOut { get; set; } = Interpolation.Linear;

        //Only read when the matching Interpolation is Bezier. The value is the
        //FRACTION OF SPEED REMOVED at that end of the segment: 0 leaves the
        //endpoint moving at the segment's average speed (identical to Linear),
        //0.5 halves it, and 1 brings it to a complete stop.
        //
        //Defaults to 1 — a full ease — because that is what selecting Bezier
        //normally means to someone: decelerate into the keyframe, accelerate out
        //of it. A partial value still eases, but the keyframe is passed at
        //non-zero speed, so if the direction of travel changes there the result
        //reads as a kink rather than a smooth turn. At 0.5 the endpoint speed is
        //half the average, which is a real but easily-missed ease.
        //
        //NOTE: easing is per-segment and there is no velocity matching ACROSS
        //keyframes. At 1 every keyframe is a smooth stop; motion never flows
        //through one at constant speed.
        public float EaseInStrength { get; set; } = 1f;

        public float EaseOutStrength { get; set; } = 1f;

        //deep copy, including the Transform — clip fragments produced by a split
        //re-time and mutate their own keyframes, so sharing either the Keyframe
        //objects or their Transforms between fragments would make editing one
        //silently animate the other
        public Keyframe Duplicate() => new()
        {
            Transform = Transform.Duplicate(),
            Start = Start,
            InterpolationIn = InterpolationIn,
            InterpolationOut = InterpolationOut,
            EaseInStrength = EaseInStrength,
            EaseOutStrength = EaseOutStrength,
        };
    }

    public enum Interpolation
    {
        Constant,
        Linear,
        Bezier
    }
}
