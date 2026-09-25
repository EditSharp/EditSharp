using System;
using System.Collections.Generic;
using System.Numerics;
using SkiaSharp;

namespace EditSharp.Components
{
    /// <summary>The arithmetic keyframes need for a value type.</summary>
    /// <remarks>Register one for a new type with <see cref="Interpolators.Register"/>; float, Vector2 and SKColor are built in. Handle offsets are values of the type too, so the arithmetic must work outside the type's usual range.</remarks>
    /// <typeparam name="T">The type of value.</typeparam>
    public interface IInterpolator<T>
    {
        /// <summary>The sum of two values.</summary>
        /// <param name="a">The first value.</param>
        /// <param name="b">The second value.</param>
        /// <returns><paramref name="a"/> + <paramref name="b"/>.</returns>
        T Add(T a, T b);

        /// <summary>The difference of two values.</summary>
        /// <param name="a">The value subtracted from.</param>
        /// <param name="b">The value subtracted.</param>
        /// <returns><paramref name="a"/> - <paramref name="b"/>.</returns>
        T Subtract(T a, T b);

        /// <summary>A value multiplied by a number.</summary>
        /// <param name="a">The value.</param>
        /// <param name="s">The multiplier.</param>
        /// <returns><paramref name="a"/> × <paramref name="s"/>.</returns>
        T Scale(T a, float s);

        /// <summary>A value part way between two others.</summary>
        /// <param name="a">The value at 0.</param>
        /// <param name="b">The value at 1.</param>
        /// <param name="t">How far from <paramref name="a"/> to <paramref name="b"/>.</param>
        /// <returns>The value.</returns>
        T Lerp(T a, T b, float t);
    }

    internal sealed class FloatInterpolator : IInterpolator<float>
    {
        public float Add(float a, float b) => a + b;
        public float Subtract(float a, float b) => a - b;
        public float Scale(float a, float s) => a * s;
        public float Lerp(float a, float b, float t) => a + ((b - a) * t);
    }

    internal sealed class Vector2Interpolator : IInterpolator<Vector2>
    {
        public Vector2 Add(Vector2 a, Vector2 b) => a + b;
        public Vector2 Subtract(Vector2 a, Vector2 b) => a - b;
        public Vector2 Scale(Vector2 a, float s) => a * s;
        public Vector2 Lerp(Vector2 a, Vector2 b, float t) => a + ((b - a) * t);
    }

    //per channel, alpha included, clamped and rounded to bytes at the end
    internal sealed class ColorInterpolator : IInterpolator<SKColor>
    {
        public SKColor Add(SKColor a, SKColor b) => FromChannels(
            a.Red + b.Red, a.Green + b.Green, a.Blue + b.Blue, a.Alpha + b.Alpha);

        public SKColor Subtract(SKColor a, SKColor b) => FromChannels(
            a.Red - b.Red, a.Green - b.Green, a.Blue - b.Blue, a.Alpha - b.Alpha);

        public SKColor Scale(SKColor a, float s) => FromChannels(
            a.Red * s, a.Green * s, a.Blue * s, a.Alpha * s);

        public SKColor Lerp(SKColor a, SKColor b, float t) => FromChannels(
            a.Red + ((b.Red - a.Red) * t),
            a.Green + ((b.Green - a.Green) * t),
            a.Blue + ((b.Blue - a.Blue) * t),
            a.Alpha + ((b.Alpha - a.Alpha) * t));

        private static SKColor FromChannels(float r, float g, float b, float a) => new(
            (byte)Math.Clamp(Math.Round(r), 0, 255),
            (byte)Math.Clamp(Math.Round(g), 0, 255),
            (byte)Math.Clamp(Math.Round(b), 0, 255),
            (byte)Math.Clamp(Math.Round(a), 0, 255));
    }

    /// <summary>The <see cref="IInterpolator{T}"/> for each value type keyframes can hold.</summary>
    public static class Interpolators
    {
        private static readonly Dictionary<Type, object> Registry = new()
        {
            [typeof(float)] = new FloatInterpolator(),
            [typeof(Vector2)] = new Vector2Interpolator(),
            [typeof(SKColor)] = new ColorInterpolator(),
        };

        /// <summary>Makes a value type keyframeable, or replaces its arithmetic.</summary>
        /// <typeparam name="T">The type of value.</typeparam>
        /// <param name="interpolator">The arithmetic for <typeparamref name="T"/>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="interpolator"/> is null.</exception>
        public static void Register<T>(IInterpolator<T> interpolator) =>
            Registry[typeof(T)] = interpolator ?? throw new ArgumentNullException(nameof(interpolator));

        /// <summary>The arithmetic registered for a value type.</summary>
        /// <typeparam name="T">The type of value.</typeparam>
        /// <returns>The interpolator.</returns>
        /// <exception cref="NotSupportedException">None is registered for <typeparamref name="T"/>.</exception>
        public static IInterpolator<T> Resolve<T>()
        {
            if (Registry.TryGetValue(typeof(T), out object? found)) return (IInterpolator<T>)found;

            throw new NotSupportedException(
                $"No IInterpolator<{typeof(T).Name}> is registered. Animatable<{typeof(T).Name}> " +
                "needs one registered with Interpolators.Register<T>() before it can be keyframed; " +
                "a static value works without one.");
        }
    }

    /// <summary>The shape of the curve on one side of a keyframe.</summary>
    public enum InterpolationType
    {
        /// <summary>No change: leaving a keyframe, its value holds until the next one.</summary>
        Hold,

        /// <summary>A straight line.</summary>
        Linear,

        /// <summary>A curve shaped by the keyframe's handle on that side.</summary>
        Bezier,
    }

    /// <summary>How a keyframe's handle on one side is set.</summary>
    public enum TangentMode
    {
        /// <summary>As set, independent of the other side.</summary>
        Free,

        /// <summary>Mirrors the other side's handle through the keyframe, so the curve stays continuous.</summary>
        Smooth,

        /// <summary>Computed from the neighbouring keyframes, and updated when they change.</summary>
        Auto,
    }
}
