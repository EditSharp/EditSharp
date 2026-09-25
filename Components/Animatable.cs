using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.History;
 
namespace EditSharp.Components
{
    /// <summary>
    /// The general per-property wrapper — see the schema doc's Keyframes
    /// section. Wrapping a property in Animatable&lt;T&gt; is the only step
    /// required to make it keyframeable; nothing about KeyframeTrack&lt;T&gt;
    /// itself needs to change for a new property type (as long as an
    /// IInterpolator&lt;T&gt; is registered — see Interpolators.Resolve).
    /// </summary>
    /// <summary>
    /// The non-generic face of Animatable&lt;T&gt;: what a Node hands out so
    /// a clip can reach every keyframe track it owns without knowing the
    /// value types — see Node.Animatables and Clip.OnHeadInPointShift.
    /// </summary>
    /// <summary>
    /// A keyframe seen without its value type - what an inspector's key
    /// column or a thumbnail cache needs: when it is, and what it holds.
    /// </summary>
    public interface IKeyframe
    {
        TimeSpan Start { get; }
        object? Value { get; }
    }

    public interface IAnimatable
    {
        /// <summary>Moves every keyframe by `amount`. Keyframes are anchored to the content, so a head trim/extend shifts them all.</summary>
        void ShiftKeyframes(TimeSpan amount);

        /// <summary>The T in Animatable&lt;T&gt;.</summary>
        Type ValueType { get; }

        /// <summary>True once the track has enough keyframes to override the static value.</summary>
        bool IsAnimated { get; }

        /// <summary>The static value, untyped — for editors that only know the descriptor.</summary>
        object? GetStaticValue();

        /// <summary>Writes the static value from an untyped editor, coercing numbers and enums.</summary>
        void SetStaticValue(object? value);

        /// <summary>
        /// Every keyframe on this property, in time order; empty when it
        /// has no track. Times are content-relative, like everything else
        /// on a track.
        /// </summary>
        IReadOnlyList<IKeyframe> Keyframes { get; }

        /// <summary>The value at a content time, keyframes or not.</summary>
        object? Evaluate(TimeSpan clipRelativeTime);

        /// <summary>
        /// Adds a keyframe at the time, or updates the one already there.
        /// Creates the track if there was none.
        /// </summary>
        void SetKeyframe(TimeSpan time, object? value);

        /// <summary>Removes the keyframe at exactly this time, if any.</summary>
        bool RemoveKeyframeAt(TimeSpan time);

        /// <summary>Drops the track: no keyframes, the static value alone.</summary>
        void ClearKeyframes();
    }

    public sealed class Animatable<T> : IAnimatable
    {
        //used when there's no track, or fewer than 2 keyframes
        T _staticValue;
        public T StaticValue { get => _staticValue; set => Transaction.Set(this, ref _staticValue, value, static (o, v) => o._staticValue = v); }
 
        //null = not animated
        KeyframeTrack<T>? _track;
        public KeyframeTrack<T>? Track { get => _track; private set => Transaction.Set(this, ref _track, value, static (o, v) => o._track = v); }
 
        public Animatable(T staticValue)
        {
            _staticValue = staticValue;
        }
 
        public static implicit operator Animatable<T>(T value) => new(value);
 
        /// <summary>
        /// Lazily creates the backing track on first use — a caller adding
        /// its first keyframe shouldn't have to separately new up a
        /// KeyframeTrack&lt;T&gt; first.
        /// </summary>
        public KeyframeTrack<T> GetOrCreateTrack() => Track ??= new KeyframeTrack<T>();
 
        /// <summary>
        /// Installs an already-constructed track — the escape hatch a
        /// KeyframeTrack&lt;T&gt; SUBCLASS (e.g. PositionTrack, which needs
        /// spatial handles beyond the plain track GetOrCreateTrack would
        /// build) needs to become this property's backing track.
        /// </summary>
        public void AttachTrack(KeyframeTrack<T> track) => Track = track ?? throw new ArgumentNullException(nameof(track));
 
        /// <summary>
        /// Clears the track entirely, falling back to StaticValue — the
        /// schema doc leaves it open whether an empty track persists or is
        /// cleared to null; this is the explicit "clear it" path for a
        /// caller that wants that.
        /// </summary>
        public void ClearTrack() => Track = null;
 
        public T Evaluate(TimeSpan clipRelativeTime)
        {
            //fewer than 2 keyframes: StaticValue wins, per the schema doc —
            //this covers both "no Track at all" and "Track exists but has 0
            //or 1 keyframes," e.g. right after RemoveKeyframe empties it
            if (Track == null || Track.Keyframes.Count < 2) return StaticValue;
 
            return Track.Evaluate(clipRelativeTime);
        }
 
        public void ShiftKeyframes(TimeSpan amount) => Track?.Shift(amount);

        public Type ValueType => typeof(T);

        public bool IsAnimated => Track is not null && Track.Keyframes.Count >= 2;

        public object? GetStaticValue() => StaticValue;

        public void SetStaticValue(object? value) => StaticValue = (T)Editing.PropertyDescriptor.Coerce(value, typeof(T))!;

        public IReadOnlyList<IKeyframe> Keyframes => Track?.Keyframes ?? (IReadOnlyList<IKeyframe>)Array.Empty<IKeyframe>();

        object? IAnimatable.Evaluate(TimeSpan clipRelativeTime) => Evaluate(clipRelativeTime);

        public void SetKeyframe(TimeSpan time, object? value)
            => GetOrCreateTrack().AddKeyframe(time, (T)Editing.PropertyDescriptor.Coerce(value, typeof(T))!);

        public void ClearKeyframes() => ClearTrack();

        public bool RemoveKeyframeAt(TimeSpan time)
        {
            Keyframe<T>? keyframe = Track?.Keyframes.FirstOrDefault(k => k.Start == time);
            if (keyframe is null) return false;
            Track!.RemoveKeyframe(keyframe);
            return true;
        }

        /// <summary>
        /// Deep copy. The track copies itself so a subclass (PositionTrack)
        /// survives as that subclass, spatial handles and all — rebuilding a
        /// plain KeyframeTrack here is what used to flatten a split
        /// fragment's motion path into straight lines.
        /// </summary>
        public Animatable<T> Duplicate()
        {
            using var _ = Transaction.Suppress();

            var copy = new Animatable<T>(StaticValue);
            if (Track != null) copy.AttachTrack(Track.Duplicate());
            return copy;
        }
    }
}
 