using System;
using System.Collections.Generic;
using System.Numerics;
using SkiaSharp;

namespace EditSharp.Components
{
    /// <summary>
    /// The arithmetic Animatable&lt;T&gt;/KeyframeTrack&lt;T&gt; need to do their job:
    /// add/subtract two values (for handle offsets, which are stored as deltas
    /// from their keyframe's own value) and blend two values by a normalized
    /// progress (for the cubic evaluation itself).
    ///
    /// Deliberately a small strategy interface resolved per-T, NOT a
    /// self-referential generic constraint (`T : IInterpolatable&lt;T&gt;`) —
    /// the three real payload types this schema keyframes (float, Vector2,
    /// SKColor) are all BCL/SkiaSharp types this package doesn't own, so they
    /// can't be made to declare an interface themselves. See Interpolators
    /// below for the registry that resolves one of these per T.
    /// </summary>
    public interface IInterpolator<T>
    {
        T Add(T a, T b);
        T Subtract(T a, T b);
        T Scale(T a, float s);
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

    /// <summary>
    /// SKColor has no arithmetic of its own, so channel math is done in plain
    /// floats (including alpha) and only clamped/rounded back to bytes at the
    /// end — Add/Subtract can legitimately produce an intermediate value
    /// outside 0-255 (a handle's ValueOffset is a delta, not a color in its
    /// own right), and only the final materialized SKColor needs to be a
    /// valid one.
    /// </summary>
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

    /// <summary>
    /// Resolves the IInterpolator&lt;T&gt; for a payload type. Covers the three
    /// types this schema actually keyframes today (float, Vector2, SKColor —
    /// see the Effects/Keyframes sections of the schema doc). A new
    /// Animatable&lt;T&gt; payload type needs one line registered here; that is
    /// the entire cost of the "new properties can easily become animatable"
    /// goal for anything that isn't one of these three already.
    /// </summary>
    public static class Interpolators
    {
        private static readonly Dictionary<Type, object> Registry = new()
        {
            [typeof(float)] = new FloatInterpolator(),
            [typeof(Vector2)] = new Vector2Interpolator(),
            [typeof(SKColor)] = new ColorInterpolator(),
        };

        public static void Register<T>(IInterpolator<T> interpolator) =>
            Registry[typeof(T)] = interpolator ?? throw new ArgumentNullException(nameof(interpolator));

        public static IInterpolator<T> Resolve<T>()
        {
            if (Registry.TryGetValue(typeof(T), out object? found)) return (IInterpolator<T>)found;

            throw new NotSupportedException(
                $"No IInterpolator<{typeof(T).Name}> is registered. Animatable<{typeof(T).Name}> " +
                "needs one registered via Interpolators.Register<T>() before it can be keyframed " +
                "(a static value with no Track works regardless).");
        }
    }

    public enum InterpolationType
    {
        Hold,
        Linear,
        Bezier,
    }

    public enum TangentMode
    {
        Free,
        Smooth,
        Auto,
    }
}
