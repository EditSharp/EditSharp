using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.History;

namespace EditSharp.Components
{
    /// <summary>A keyframe seen without its value type.</summary>
    public interface IKeyframe
    {
        /// <summary>When the keyframe is, in content time.</summary>
        Time Start { get; }

        /// <summary>The value it holds.</summary>
        object? Value { get; }
    }

    /// <summary>A keyframeable value seen without its value type, for editors and for code that reaches every keyframe at once.</summary>
    public interface IAnimatable
    {
        /// <summary>Moves every keyframe by the same amount.</summary>
        /// <param name="amount">How far to move them; negative moves them earlier.</param>
        void ShiftKeyframes(Time amount);

        /// <summary>The type of value it holds.</summary>
        Type ValueType { get; }

        /// <summary>Whether it has at least two keyframes, so they decide its value instead of the static value.</summary>
        bool IsAnimated { get; }

        /// <summary>The value used when it isn't animated.</summary>
        /// <returns>The static value.</returns>
        object? GetStaticValue();

        /// <summary>Sets the value used when it isn't animated.</summary>
        /// <param name="value">The value; numbers and enums are converted to the value type.</param>
        void SetStaticValue(object? value);

        /// <summary>Every keyframe, in time order; empty when there's no track.</summary>
        IReadOnlyList<IKeyframe> Keyframes { get; }

        /// <summary>The value at a moment.</summary>
        /// <param name="clipRelativeTime">Content time: since the clip's in-point, at 1x.</param>
        /// <returns>The value.</returns>
        object? Evaluate(Time clipRelativeTime);

        /// <summary>Adds a keyframe, or updates the one already at that time.</summary>
        /// <param name="time">When, in content time.</param>
        /// <param name="value">The value; numbers and enums are converted to the value type.</param>
        void SetKeyframe(Time time, object? value);

        /// <summary>Removes the keyframe at exactly a time.</summary>
        /// <param name="time">When, in content time.</param>
        /// <returns>False when there was no keyframe at that time.</returns>
        bool RemoveKeyframeAt(Time time);

        /// <summary>Removes every keyframe, leaving the static value.</summary>
        void ClearKeyframes();
    }

    /// <summary>A value that can be keyframed: a static value, plus an optional track of keyframes.</summary>
    /// <remarks>With fewer than two keyframes the static value is used. Keyframing a value of a new type needs an <see cref="IInterpolator{T}"/> registered with <see cref="Interpolators.Register"/>.</remarks>
    /// <typeparam name="T">The type of value.</typeparam>
    public sealed class Animatable<T> : IAnimatable
    {
        T _staticValue;
        /// <summary>The value used when there are fewer than two keyframes.</summary>
        public T StaticValue { get => _staticValue; set => Transaction.Set(this, ref _staticValue, value, static (o, v) => o._staticValue = v); }

        KeyframeTrack<T>? _track;
        /// <summary>The keyframes; null when there are none.</summary>
        public KeyframeTrack<T>? Track { get => _track; private set => Transaction.Set(this, ref _track, value, static (o, v) => o._track = v); }

        /// <summary>A value with no keyframes.</summary>
        /// <param name="staticValue">The value.</param>
        public Animatable(T staticValue)
        {
            _staticValue = staticValue;
        }

        /// <summary>A value with no keyframes.</summary>
        /// <param name="value">The value.</param>
        public static implicit operator Animatable<T>(T value) => new(value);

        /// <summary>The track, created empty if there isn't one.</summary>
        /// <returns>The track.</returns>
        public KeyframeTrack<T> GetOrCreateTrack() => Track ??= new KeyframeTrack<T>();

        /// <summary>Replaces the track, such as with a <see cref="PositionTrack"/>.</summary>
        /// <param name="track">The new track.</param>
        /// <exception cref="ArgumentNullException"><paramref name="track"/> is null.</exception>
        public void AttachTrack(KeyframeTrack<T> track) => Track = track ?? throw new ArgumentNullException(nameof(track));

        /// <summary>Removes the track, leaving the static value.</summary>
        public void ClearTrack() => Track = null;

        /// <summary>The value at a moment: the track's when it has two or more keyframes, otherwise the static value.</summary>
        /// <param name="clipRelativeTime">Content time: since the clip's in-point, at 1x.</param>
        /// <returns>The value.</returns>
        public T Evaluate(Time clipRelativeTime)
        {
            if (Track == null || Track.Keyframes.Count < 2) return StaticValue;

            return Track.Evaluate(clipRelativeTime);
        }

        /// <inheritdoc/>
        public void ShiftKeyframes(Time amount) => Track?.Shift(amount);

        /// <inheritdoc/>
        public Type ValueType => typeof(T);

        /// <inheritdoc/>
        public bool IsAnimated => Track is not null && Track.Keyframes.Count >= 2;

        /// <inheritdoc/>
        public object? GetStaticValue() => StaticValue;

        /// <inheritdoc/>
        public void SetStaticValue(object? value) => StaticValue = (T)Editing.PropertyDescriptor.Coerce(value, typeof(T))!;

        /// <inheritdoc/>
        public IReadOnlyList<IKeyframe> Keyframes => Track?.Keyframes ?? (IReadOnlyList<IKeyframe>)Array.Empty<IKeyframe>();

        object? IAnimatable.Evaluate(Time clipRelativeTime) => Evaluate(clipRelativeTime);

        /// <inheritdoc/>
        public void SetKeyframe(Time time, object? value)
            => GetOrCreateTrack().AddKeyframe(time, (T)Editing.PropertyDescriptor.Coerce(value, typeof(T))!);

        /// <inheritdoc/>
        public void ClearKeyframes() => ClearTrack();

        /// <inheritdoc/>
        public bool RemoveKeyframeAt(Time time)
        {
            Keyframe<T>? keyframe = Track?.Keyframes.FirstOrDefault(k => k.Start == time);
            if (keyframe is null) return false;
            Track!.RemoveKeyframe(keyframe);
            return true;
        }

        /// <summary>A deep copy; the track keeps its type, so a PositionTrack stays one.</summary>
        /// <remarks>Nothing is recorded in history.</remarks>
        /// <returns>The copy.</returns>
        public Animatable<T> Duplicate()
        {
            using var _ = Transaction.Suppress();

            var copy = new Animatable<T>(StaticValue);
            if (Track != null) copy.AttachTrack(Track.Duplicate());
            return copy;
        }
    }
}
