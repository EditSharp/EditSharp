using System;
 
namespace EditSharp.Components
{
    /// <summary>
    /// The general per-property wrapper — see the schema doc's Keyframes
    /// section. Wrapping a property in Animatable&lt;T&gt; is the only step
    /// required to make it keyframeable; nothing about KeyframeTrack&lt;T&gt;
    /// itself needs to change for a new property type (as long as an
    /// IInterpolator&lt;T&gt; is registered — see Interpolators.Resolve).
    /// </summary>
    public sealed class Animatable<T>
    {
        //used when there's no track, or fewer than 2 keyframes
        public T StaticValue { get; set; }
 
        //null = not animated
        public KeyframeTrack<T>? Track { get; private set; }
 
        public Animatable(T staticValue)
        {
            StaticValue = staticValue;
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
 
        public Animatable<T> Duplicate()
        {
            var copy = new Animatable<T>(StaticValue);
            if (Track == null) return copy;
 
            KeyframeTrack<T> newTrack = copy.GetOrCreateTrack();
            foreach (Keyframe<T> kf in Track.Keyframes)
            {
                Keyframe<T> added = newTrack.AddKeyframe(kf.Start, kf.Value);
                added.InInterpolation = kf.InInterpolation;
                added.OutInterpolation = kf.OutInterpolation;
                added.InTangentMode = kf.InTangentMode;
                added.OutTangentMode = kf.OutTangentMode;
 
                if (kf.InHandle != null)
                    newTrack.SetHandle(added, isInHandle: true, kf.InHandle.TimeOffset, kf.InHandle.ValueOffset);
                if (kf.OutHandle != null)
                    newTrack.SetHandle(added, isInHandle: false, kf.OutHandle.TimeOffset, kf.OutHandle.ValueOffset);
 
                //SetHandle above promotes Auto -> Free as a side effect (see
                //its own remarks) — restore the source's real tangent modes
                //now that both handles are copied
                added.InTangentMode = kf.InTangentMode;
                added.OutTangentMode = kf.OutTangentMode;
            }
 
            return copy;
        }
    }
}
 