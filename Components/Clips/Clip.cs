using SkiaSharp;
using EditSharp.Components.Effects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace EditSharp.Components.Clips
{
    public abstract class Clip
    {
        //how long after the beginning of the channel to place this clip
        public required TimeSpan Start { get; set; }

        //how long the clip should last
        public required TimeSpan Duration { get; set; }

        //the time at which the clip ends
        public TimeSpan End
        {
            get
            {
                return Start + Duration;
            }
            set
            {
                //clip cannot have a duration of zero or less
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, Start);

                Duration = value - Start;
            }
        }

        //color to modulate the clip to (can also be used for transparency)
        public SKColor Modulate = SKColors.White;

        //base state of the clip. only used when Keyframes is empty — once
        //keyframes exist they define the clip's state entirely and this is ignored
        public ClipTransform Transform { get; set; } = new();

        //clip states to animate between. Keyframe.Start is relative to this
        //clip's own beginning, not the channel's
        public List<Keyframe> Keyframes = [];

        //list of effects to apply
        public List<Effect> Effects { get; set; } = [];

        //return a new Clip object with the exact properties of this one.
        //must deep-copy everything a split fragment could go on to mutate
        //independently (Transform, Keyframes, Effects, and any Source)
        public abstract Clip Duplicate();

        /// <summary>
        /// Called when the clip's head is trimmed, so subclasses holding media can
        /// advance their in-point by the same amount. Without this, trimming the
        /// head of a source clip would replay the material that was just cut off.
        /// </summary>
        protected virtual void OnTrimmedFromStart(TimeSpan amount) { }

        /// <summary>
        /// The clip's transform at a given time relative to its own start.
        /// With no keyframes this is just Transform. With keyframes, the pair
        /// bracketing the requested time is interpolated: the segment's shape at
        /// its START comes from the earlier keyframe's InterpolationOut, and at
        /// its END from the later keyframe's InterpolationIn — so a keyframe can
        /// be left one way and entered another. Before the first keyframe and
        /// after the last, the nearest keyframe's value is held.
        /// </summary>
        public ClipTransform TransformAt(TimeSpan time)
        {
            if (Keyframes.Count == 0) return Transform.Duplicate();

            List<Keyframe> ordered = [.. Keyframes.OrderBy(k => k.Start)];

            if (time <= ordered[0].Start) return ordered[0].Transform.Duplicate();
            if (time >= ordered[^1].Start) return ordered[^1].Transform.Duplicate();

            for (int i = 0; i < ordered.Count - 1; i++)
            {
                Keyframe from = ordered[i];
                Keyframe to = ordered[i + 1];
                if (time < from.Start || time > to.Start) continue;

                double span = (to.Start - from.Start).TotalSeconds;
                double u = span <= 0 ? 1.0 : (time - from.Start).TotalSeconds / span;

                double eased = Ease(
                    u,
                    from.InterpolationOut, from.EaseOutStrength,
                    to.InterpolationIn, to.EaseInStrength);

                return ClipTransform.Lerp(from.Transform, to.Transform, eased);
            }

            return ordered[^1].Transform.Duplicate();
        }

        /// <summary>
        /// Maps normalized segment progress (0-1) through the easing described by
        /// the two keyframes bounding it, as a cubic evaluated directly against
        /// normalized time.
        ///
        /// Deliberately NOT a CSS-style cubic-bezier: that form requires solving
        /// for the curve parameter given elapsed time, which is iterative, and
        /// these same curves have to be emitted as ffmpeg filter expressions where
        /// iteration isn't available. Evaluating the cubic directly against time
        /// is closed-form, so the C# result here and the generated expression
        /// agree exactly.
        ///
        /// Control values are placed so that strength 0 reproduces linear motion
        /// precisely (p1=1/3, p2=2/3 makes the cubic collapse to u), and strength
        /// 1 puts the curve fully flat at that endpoint.
        /// </summary>
        public static double Ease(
            double u,
            Interpolation outInterpolation, float outStrength,
            Interpolation inInterpolation, float inStrength)
        {
            //a Constant on either side means no motion at all across the segment —
            //the earlier value is held, then steps to the later one at its keyframe
            if (outInterpolation == Interpolation.Constant || inInterpolation == Interpolation.Constant)
                return 0.0;

            double p1 = outInterpolation == Interpolation.Bezier
                ? (1.0 - Math.Clamp(outStrength, 0f, 1f)) / 3.0
                : 1.0 / 3.0;

            double p2 = inInterpolation == Interpolation.Bezier
                ? 1.0 - ((1.0 - Math.Clamp(inStrength, 0f, 1f)) / 3.0)
                : 2.0 / 3.0;

            double m = 1.0 - u;
            return (3.0 * m * m * u * p1) + (3.0 * m * u * u * p2) + (u * u * u);
        }

        /// <summary>
        /// Removes time from the front of the clip, keeping its End where it is.
        /// Any media in-point advances by the same amount (see OnTrimmedFromStart),
        /// and keyframes are re-timed to the new start: those now in the past are
        /// dropped, and a synthesized keyframe holds the exact animated state at
        /// the cut so the remaining motion continues from where it actually was
        /// rather than snapping to the next surviving keyframe.
        /// </summary>
        public void TrimStart(TimeSpan amount)
        {
            if (amount <= TimeSpan.Zero) return;
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(amount, Duration);

            //evaluated BEFORE any mutation, while keyframe times still line up
            ClipTransform atCut = TransformAt(amount);
            Keyframe? active = LastKeyframeAtOrBefore(amount);

            Start += amount;
            Duration -= amount;
            OnTrimmedFromStart(amount);

            if (Keyframes.Count == 0) return;

            List<Keyframe> kept = [];
            foreach (Keyframe keyframe in Keyframes.OrderBy(k => k.Start))
            {
                TimeSpan shifted = keyframe.Start - amount;
                if (shifted <= TimeSpan.Zero) continue;

                keyframe.Start = shifted;
                kept.Add(keyframe);
            }

            kept.Insert(0, new Keyframe
            {
                Start = TimeSpan.Zero,
                Transform = atCut,
                InterpolationIn = Interpolation.Linear,
                InterpolationOut = active?.InterpolationOut ?? Interpolation.Linear,
                EaseOutStrength = active?.EaseOutStrength ?? 0.5f,
            });

            Keyframes = kept;
        }

        /// <summary>
        /// Removes time from the end of the clip, keeping its Start where it is.
        /// Mirrors TrimStart: keyframes past the new end are dropped and one is
        /// synthesized at the new end holding the state the animation had actually
        /// reached there. No media in-point change — the head is untouched.
        /// </summary>
        public void TrimEnd(TimeSpan amount)
        {
            if (amount <= TimeSpan.Zero) return;
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(amount, Duration);

            TimeSpan newDuration = Duration - amount;

            ClipTransform atCut = TransformAt(newDuration);
            Keyframe? next = FirstKeyframeAtOrAfter(newDuration);

            Duration = newDuration;

            if (Keyframes.Count == 0) return;

            List<Keyframe> kept = [];
            foreach (Keyframe keyframe in Keyframes.OrderBy(k => k.Start))
            {
                if (keyframe.Start >= newDuration) continue;
                kept.Add(keyframe);
            }

            kept.Add(new Keyframe
            {
                Start = newDuration,
                Transform = atCut,
                InterpolationIn = next?.InterpolationIn ?? Interpolation.Linear,
                EaseInStrength = next?.EaseInStrength ?? 0.5f,
                InterpolationOut = Interpolation.Linear,
            });

            Keyframes = kept;
        }

        /// <summary>
        /// Splits this clip around the time range [from, to), returning the two
        /// surviving fragments. This clip itself is left untouched — both
        /// fragments are duplicates, so callers must replace it in whatever
        /// collection holds it (and drop anything keyed on its identity, such as
        /// transitions, since neither fragment is the original clip).
        /// </summary>
        public (Clip Head, Clip Tail) SplitAround(TimeSpan from, TimeSpan to)
        {
            if (from <= Start || to >= End)
                throw new ArgumentOutOfRangeException(nameof(from),
                    "SplitAround requires a range strictly inside the clip — " +
                    "a range touching either edge is a trim, not a split.");

            Clip head = Duplicate();
            head.TrimEnd(head.End - from);

            Clip tail = Duplicate();
            tail.TrimStart(to - tail.Start);

            return (head, tail);
        }

        private Keyframe? LastKeyframeAtOrBefore(TimeSpan time) =>
            Keyframes.Where(k => k.Start <= time).OrderBy(k => k.Start).LastOrDefault();

        private Keyframe? FirstKeyframeAtOrAfter(TimeSpan time) =>
            Keyframes.Where(k => k.Start >= time).OrderBy(k => k.Start).FirstOrDefault();

        //returns true if this clip has the same start as the provided clip
        public bool StartsAt(Clip clip)
        {
            return Start == clip.Start;
        }

        //returns true if this clip starts inside the provided clip
        public bool StartsInside(Clip clip)
        {
            return Start > clip.Start && Start < clip.End;
        }

        //returns true if this clip's start intersects with the provided clip
        public bool StartIntersectsWith(Clip clip)
        {
            return StartsAt(clip) || StartsInside(clip);
        }

        //returns true if this clip has the same end as the provided clip
        public bool EndsAt(Clip clip)
        {
            return End == clip.End;
        }

        //returns true if this clip ends inside the provided clip
        public bool EndsInside(Clip clip)
        {
            return End > clip.Start && End < clip.End;
        }

        //returns true if this clip's end intersects with the provided clip
        public bool EndIntersectsWith(Clip clip)
        {
            return EndsAt(clip) || EndsInside(clip);
        }

        //returns true if this clip overlaps entirely with provided clip
        public bool FullyIntersects(Clip clip)
        {
            return StartIntersectsWith(clip) && EndIntersectsWith(clip);
        }

        //returns true if this clip has a longer duration than the provided clip
        public bool IsLongerThan(Clip clip)
        {
            return Duration > clip.Duration;
        }

        //returns the amount of time this clip intersects with the provided clip
        public TimeSpan IntersectionWith(Clip clip)
        {
            TimeSpan earlierEnd = End < clip.End ? End : clip.End;
            TimeSpan laterStart = Start > clip.Start ? Start : clip.Start;

            TimeSpan overlap = earlierEnd - laterStart;

            return overlap > TimeSpan.Zero ? overlap : TimeSpan.Zero;
        }
    }

    public class ClipTransform
    {
        //the on-screen position of the clip in (X, Y) coordinates
        public Vector2 Position { get; set; }

        //the on-screen scale of the clip in (X, Y) scale factor
        public Vector2 Scale { get; set; } = new(1, 1);

        //the on-screen rotation of the clip in degrees
        public float Rotation { get; set; }

        //pitch (3D vertical) of the clip from -180 degrees to 180 degrees
        public float Pitch { get; set; }

        //yaw (3D horizontal) of the clip from -180 degrees to 180 degrees
        public float Yaw { get; set; }

        public ClipTransform Duplicate() => new()
        {
            Position = Position,
            Scale = Scale,
            Rotation = Rotation,
            Pitch = Pitch,
            Yaw = Yaw,
        };

        /// <summary>
        /// Blends two transforms by an already-eased progress value. Every
        /// property interpolates independently and linearly — the easing curve is
        /// applied to `t` by the caller, not here.
        /// </summary>
        public static ClipTransform Lerp(ClipTransform from, ClipTransform to, double t)
        {
            float f = (float)t;

            return new ClipTransform
            {
                Position = new Vector2(
                    from.Position.X + ((to.Position.X - from.Position.X) * f),
                    from.Position.Y + ((to.Position.Y - from.Position.Y) * f)),
                Scale = new Vector2(
                    from.Scale.X + ((to.Scale.X - from.Scale.X) * f),
                    from.Scale.Y + ((to.Scale.Y - from.Scale.Y) * f)),
                Rotation = from.Rotation + ((to.Rotation - from.Rotation) * f),
                Pitch = from.Pitch + ((to.Pitch - from.Pitch) * f),
                Yaw = from.Yaw + ((to.Yaw - from.Yaw) * f),
            };
        }

        public static Vector2 FromPosition(Positions position)
        {
            return PositionToVector2[position];
        }

        static readonly Dictionary<Positions, Vector2> PositionToVector2 = new()
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
        TopLeft,
        TopCenter,
        TopRight,
        CenterLeft,
        CenterMiddle,
        CenterRight,
        BottomLeft,
        BottomCenter,
        BottomRight
    }
}
