using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.History;

namespace EditSharp.Components
{
    /// <summary>A Bezier handle: where the curve's control point sits relative to its keyframe.</summary>
    /// <typeparam name="T">The type of value.</typeparam>
    public sealed class KeyframeHandle<T>
    {
        /// <summary>How far the handle reaches toward the neighbouring keyframe on its side; never negative, and never past that keyframe.</summary>
        public TimeSpan TimeOffset { get; internal set; }

        /// <summary>The control point's value, as an offset added to the keyframe's value.</summary>
        public T ValueOffset { get; internal set; }

        internal KeyframeHandle(TimeSpan timeOffset, T valueOffset)
        {
            TimeOffset = timeOffset;
            ValueOffset = valueOffset;
        }
    }

    /// <summary>A value at a moment on a <see cref="KeyframeTrack{T}"/>.</summary>
    /// <remarks>Each side has its own interpolation: a segment's shape comes from the left keyframe's <see cref="OutInterpolation"/> and the right keyframe's <see cref="InInterpolation"/>.</remarks>
    /// <typeparam name="T">The type of value.</typeparam>
    public class Keyframe<T> : IKeyframe
    {
        object? IKeyframe.Value => Value;

        TimeSpan _start;
        /// <summary>When the keyframe is, in content time; move it with <see cref="KeyframeTrack{T}.MoveKeyframe"/>.</summary>
        public TimeSpan Start { get => _start; internal set => Transaction.Set(this, ref _start, value, static (o, v) => o._start = v); }

        T _value;
        /// <summary>The value.</summary>
        public T Value { get => _value; set => Transaction.Set(this, ref _value, value, static (o, v) => o._value = v); }

        InterpolationType _inInterpolation = InterpolationType.Linear;
        /// <summary>The shape of the curve arriving at this keyframe.</summary>
        public InterpolationType InInterpolation { get => _inInterpolation; set => Transaction.Set(this, ref _inInterpolation, value, static (o, v) => o._inInterpolation = v); }
        InterpolationType _outInterpolation = InterpolationType.Linear;
        /// <summary>The shape of the curve leaving this keyframe; Hold here holds the value until the next keyframe.</summary>
        public InterpolationType OutInterpolation { get => _outInterpolation; set => Transaction.Set(this, ref _outInterpolation, value, static (o, v) => o._outInterpolation = v); }

        KeyframeHandle<T>? _inHandle;
        /// <summary>The handle on the arriving side; used when <see cref="InInterpolation"/> is Bezier.</summary>
        public KeyframeHandle<T>? InHandle { get => _inHandle; internal set => Transaction.Set(this, ref _inHandle, value, static (o, v) => o._inHandle = v); }
        KeyframeHandle<T>? _outHandle;
        /// <summary>The handle on the leaving side; used when <see cref="OutInterpolation"/> is Bezier.</summary>
        public KeyframeHandle<T>? OutHandle { get => _outHandle; internal set => Transaction.Set(this, ref _outHandle, value, static (o, v) => o._outHandle = v); }

        TangentMode _inTangentMode = TangentMode.Auto;
        /// <summary>How the arriving handle is set; see <see cref="KeyframeTrack{T}.SetTangentMode"/>.</summary>
        public TangentMode InTangentMode { get => _inTangentMode; internal set => Transaction.Set(this, ref _inTangentMode, value, static (o, v) => o._inTangentMode = v); }
        TangentMode _outTangentMode = TangentMode.Auto;
        /// <summary>How the leaving handle is set; see <see cref="KeyframeTrack{T}.SetTangentMode"/>.</summary>
        public TangentMode OutTangentMode { get => _outTangentMode; internal set => Transaction.Set(this, ref _outTangentMode, value, static (o, v) => o._outTangentMode = v); }

        internal Keyframe(TimeSpan start, T value)
        {
            _start = start;
            _value = value;
        }
    }

    /// <summary>One value's keyframes, in time order.</summary>
    /// <remarks>Keyframes change only through the track's methods, which keep them in order, keep two from sharing a time, keep handles from reaching past a neighbour, and update Auto handles when a neighbour changes. Every change is recorded in history.</remarks>
    /// <typeparam name="T">The type of value.</typeparam>
    public class KeyframeTrack<T>
    {
        private readonly IInterpolator<T> _interpolator;
        private readonly List<Keyframe<T>> _keyframes = [];

        /// <summary>The keyframes, in time order.</summary>
        public IReadOnlyList<Keyframe<T>> Keyframes => _keyframes;

        /// <summary>An empty track using the <see cref="IInterpolator{T}"/> registered for <typeparamref name="T"/>.</summary>
        /// <exception cref="NotSupportedException">No interpolator is registered for <typeparamref name="T"/>.</exception>
        public KeyframeTrack() : this(Interpolators.Resolve<T>()) { }

        /// <summary>An empty track using a given interpolator.</summary>
        /// <param name="interpolator">The arithmetic for <typeparamref name="T"/>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="interpolator"/> is null.</exception>
        public KeyframeTrack(IInterpolator<T> interpolator)
        {
            _interpolator = interpolator ?? throw new ArgumentNullException(nameof(interpolator));
        }

        /// <summary>Adds a keyframe, or updates the value of the one already at that time.</summary>
        /// <param name="start">When, in content time.</param>
        /// <param name="value">The value.</param>
        /// <returns>The keyframe.</returns>
        public Keyframe<T> AddKeyframe(TimeSpan start, T value)
        {
            Keyframe<T>? existing = _keyframes.FirstOrDefault(k => k.Start == start);
            if (existing != null)
            {
                existing.Value = value;
                return existing;
            }

            Keyframe<T> created = CreateKeyframe(start, value);

            int insertAt = _keyframes.FindIndex(k => k.Start > start);
            if (insertAt < 0) insertAt = _keyframes.Count;

            Transaction.Apply(
                () => _keyframes.Insert(Math.Min(insertAt, _keyframes.Count), created),
                () => _keyframes.Remove(created),
                "add keyframe");

            //a new keyframe changes its neighbours' Auto handles too
            int index = _keyframes.IndexOf(created);
            RecomputeAutoHandles(created);
            if (index > 0) RecomputeAutoHandles(_keyframes[index - 1]);
            if (index < _keyframes.Count - 1) RecomputeAutoHandles(_keyframes[index + 1]);

            return created;
        }

        /// <summary>Makes a new keyframe; a subclass can return its own kind, as <see cref="PositionTrack"/> does.</summary>
        /// <param name="start">When, in content time.</param>
        /// <param name="value">The value.</param>
        /// <returns>The keyframe.</returns>
        protected virtual Keyframe<T> CreateKeyframe(TimeSpan start, T value) => new(start, value);

        /// <summary>Moves every keyframe by the same amount, handles and all.</summary>
        /// <remarks>Keyframes can end up before zero: a head trim leaves the ones it cut past at negative times, so extending back restores them.</remarks>
        /// <param name="amount">How far to move them; negative moves them earlier.</param>
        public void Shift(TimeSpan amount)
        {
            if (amount == TimeSpan.Zero) return;

            foreach (Keyframe<T> keyframe in _keyframes) keyframe.Start += amount;
        }

        /// <summary>A deep copy of the same kind of track.</summary>
        /// <remarks>Nothing is recorded in history.</remarks>
        /// <returns>The copy.</returns>
        public KeyframeTrack<T> Duplicate()
        {
            using var _ = Transaction.Suppress();

            KeyframeTrack<T> copy = CreateEmptyCopy();

            foreach (Keyframe<T> kf in _keyframes)
            {
                Keyframe<T> added = copy.AddKeyframe(kf.Start, kf.Value);
                added.InInterpolation = kf.InInterpolation;
                added.OutInterpolation = kf.OutInterpolation;

                if (kf.InHandle != null)
                    copy.SetHandle(added, isInHandle: true, kf.InHandle.TimeOffset, kf.InHandle.ValueOffset);
                if (kf.OutHandle != null)
                    copy.SetHandle(added, isInHandle: false, kf.OutHandle.TimeOffset, kf.OutHandle.ValueOffset);

                //SetHandle turns Auto into Free; put back the real modes
                added.InTangentMode = kf.InTangentMode;
                added.OutTangentMode = kf.OutTangentMode;

                CopyKeyframeExtras(kf, added);
            }

            return copy;
        }

        /// <summary>An empty track of this kind, for <see cref="Duplicate"/>.</summary>
        /// <returns>The empty track.</returns>
        protected virtual KeyframeTrack<T> CreateEmptyCopy() => new(_interpolator);

        /// <summary>Copies whatever a subclass's keyframes hold beyond the base fields, for <see cref="Duplicate"/>.</summary>
        /// <param name="source">The keyframe copied from.</param>
        /// <param name="target">The keyframe copied to.</param>
        protected virtual void CopyKeyframeExtras(Keyframe<T> source, Keyframe<T> target) { }

        /// <summary>Removes a keyframe; one not on the track is ignored.</summary>
        /// <param name="keyframe">The keyframe.</param>
        public void RemoveKeyframe(Keyframe<T> keyframe)
        {
            int index = _keyframes.IndexOf(keyframe);
            if (index < 0) return;

            Transaction.Apply(
                () => _keyframes.Remove(keyframe),
                () => _keyframes.Insert(Math.Min(index, _keyframes.Count), keyframe),
                "remove keyframe");

            if (index > 0 && index < _keyframes.Count)
            {
                //the neighbours are now adjacent, so their Auto handles change
                RecomputeAutoHandles(_keyframes[index - 1]);
                RecomputeAutoHandles(_keyframes[index]);
            }
            else if (index > 0)
            {
                RecomputeAutoHandles(_keyframes[index - 1]);
            }
            else if (_keyframes.Count > 0)
            {
                RecomputeAutoHandles(_keyframes[0]);
            }
        }

        /// <summary>Moves a keyframe and sets its value; it stays strictly between its neighbours.</summary>
        /// <param name="keyframe">The keyframe; one not on the track is ignored.</param>
        /// <param name="newStart">The new time, clamped to just inside its neighbours.</param>
        /// <param name="newValue">The new value.</param>
        public void MoveKeyframe(Keyframe<T> keyframe, TimeSpan newStart, T newValue)
        {
            int index = _keyframes.IndexOf(keyframe);
            if (index < 0) return;

            TimeSpan floor = index > 0 ? _keyframes[index - 1].Start : TimeSpan.MinValue;
            TimeSpan ceiling = index < _keyframes.Count - 1 ? _keyframes[index + 1].Start : TimeSpan.MaxValue;

            //strictly between, since two keyframes can't share a time
            TimeSpan clampedFloor = floor == TimeSpan.MinValue ? floor : floor + TimeSpan.FromTicks(1);
            TimeSpan clampedCeiling = ceiling == TimeSpan.MaxValue ? ceiling : ceiling - TimeSpan.FromTicks(1);

            TimeSpan clamped = newStart < clampedFloor ? clampedFloor
                : newStart > clampedCeiling ? clampedCeiling
                : newStart;

            keyframe.Start = clamped;
            keyframe.Value = newValue;

            //keeps the list sorted even if the caller had it out of order
            List<Keyframe<T>> before = [.. _keyframes];
            Transaction.Apply(
                () => _keyframes.Sort((a, b) => a.Start.CompareTo(b.Start)),
                () => { _keyframes.Clear(); _keyframes.AddRange(before); },
                "reorder keyframes");

            //both neighbours' Auto handles depend on the gap to this keyframe
            int newIndex = _keyframes.IndexOf(keyframe);
            RecomputeAutoHandles(keyframe);
            if (newIndex > 0) RecomputeAutoHandles(_keyframes[newIndex - 1]);
            if (newIndex < _keyframes.Count - 1) RecomputeAutoHandles(_keyframes[newIndex + 1]);
        }

        /// <summary>Sets one of a keyframe's handles.</summary>
        /// <remarks>An Auto side becomes Free. A Smooth side mirrors the other handle through the keyframe, so the curve stays continuous.</remarks>
        /// <param name="keyframe">The keyframe; one not on the track is ignored.</param>
        /// <param name="isInHandle">True for the arriving handle, false for the leaving one.</param>
        /// <param name="timeOffset">How far the handle reaches toward the neighbour on its side, clamped to that neighbour.</param>
        /// <param name="valueOffset">The control point's value, as an offset added to the keyframe's value.</param>
        public void SetHandle(Keyframe<T> keyframe, bool isInHandle, TimeSpan timeOffset, T valueOffset)
        {
            int index = _keyframes.IndexOf(keyframe);
            if (index < 0) return;

            TimeSpan maxReach = isInHandle
                ? (index > 0 ? keyframe.Start - _keyframes[index - 1].Start : TimeSpan.MaxValue)
                : (index < _keyframes.Count - 1 ? _keyframes[index + 1].Start - keyframe.Start : TimeSpan.MaxValue);

            TimeSpan clampedOffset = timeOffset < TimeSpan.Zero ? TimeSpan.Zero
                : maxReach != TimeSpan.MaxValue && timeOffset > maxReach ? maxReach
                : timeOffset;

            var handle = new KeyframeHandle<T>(clampedOffset, valueOffset);

            if (isInHandle)
            {
                keyframe.InHandle = handle;
                if (keyframe.InTangentMode == TangentMode.Auto) keyframe.InTangentMode = TangentMode.Free;
                if (keyframe.InTangentMode == TangentMode.Smooth) MirrorHandle(keyframe, fromIn: true);
            }
            else
            {
                keyframe.OutHandle = handle;
                if (keyframe.OutTangentMode == TangentMode.Auto) keyframe.OutTangentMode = TangentMode.Free;
                if (keyframe.OutTangentMode == TangentMode.Smooth) MirrorHandle(keyframe, fromIn: false);
            }
        }

        /// <summary>Sets how one of a keyframe's handles is set.</summary>
        /// <remarks>Auto recomputes the handle from the neighbours now and whenever they change; Smooth mirrors the other handle; Free leaves it as set.</remarks>
        /// <param name="keyframe">The keyframe.</param>
        /// <param name="isIn">True for the arriving side, false for the leaving one.</param>
        /// <param name="mode">The mode.</param>
        public void SetTangentMode(Keyframe<T> keyframe, bool isIn, TangentMode mode)
        {
            if (isIn) keyframe.InTangentMode = mode;
            else keyframe.OutTangentMode = mode;

            if (mode == TangentMode.Auto) RecomputeAutoHandles(keyframe);
            else if (mode == TangentMode.Smooth) MirrorHandle(keyframe, fromIn: isIn);
        }

        private void MirrorHandle(Keyframe<T> keyframe, bool fromIn)
        {
            KeyframeHandle<T>? source = fromIn ? keyframe.InHandle : keyframe.OutHandle;
            if (source == null) return;

            var mirrored = new KeyframeHandle<T>(source.TimeOffset, _interpolator.Scale(source.ValueOffset, -1f));

            if (fromIn) keyframe.OutHandle = mirrored;
            else keyframe.InHandle = mirrored;
        }

        //Auto handles point from the previous keyframe toward the next, so the curve is continuous through
        //the keyframe; both sides are recomputed together
        private void RecomputeAutoHandles(Keyframe<T> keyframe)
        {
            int index = _keyframes.IndexOf(keyframe);
            if (index < 0) return;

            bool hasPrev = index > 0;
            bool hasNext = index < _keyframes.Count - 1;

            if (!hasPrev && !hasNext) return;

            Keyframe<T> prev = hasPrev ? _keyframes[index - 1] : keyframe;
            Keyframe<T> next = hasNext ? _keyframes[index + 1] : keyframe;

            T tangentValue = _interpolator.Scale(_interpolator.Subtract(next.Value, prev.Value), 1f / 6f);

            //an edge keyframe reaches a third of the way to its one neighbour
            TimeSpan span = hasPrev && hasNext
                ? TimeSpan.FromTicks((next.Start - prev.Start).Ticks / 6)
                : hasNext
                    ? TimeSpan.FromTicks((next.Start - keyframe.Start).Ticks / 3)
                    : TimeSpan.FromTicks((keyframe.Start - prev.Start).Ticks / 3);

            //a handle clamped to a shorter gap scales its value by the same ratio, keeping its slope; a steeper
            //handle would overshoot the next keyframe
            if (keyframe.OutTangentMode == TangentMode.Auto && hasNext)
            {
                TimeSpan gap = next.Start - keyframe.Start;
                TimeSpan reach = span.Ticks <= gap.Ticks ? span : gap;
                float scale = span.Ticks > 0 ? (float)reach.Ticks / span.Ticks : 1f;
                keyframe.OutHandle = new KeyframeHandle<T>(reach, _interpolator.Scale(tangentValue, scale));
            }

            if (keyframe.InTangentMode == TangentMode.Auto && hasPrev)
            {
                TimeSpan gap = keyframe.Start - prev.Start;
                TimeSpan reach = span.Ticks <= gap.Ticks ? span : gap;
                float scale = span.Ticks > 0 ? (float)reach.Ticks / span.Ticks : 1f;
                keyframe.InHandle = new KeyframeHandle<T>(reach, _interpolator.Scale(tangentValue, -scale));
            }
        }

        /// <summary>The value at a moment.</summary>
        /// <remarks>Before the first keyframe it's the first keyframe's value, after the last the last's. <see cref="Animatable{T}"/> uses its static value instead when there are fewer than two keyframes.</remarks>
        /// <param name="time">Content time.</param>
        /// <returns>The value; default when there are no keyframes.</returns>
        public T Evaluate(TimeSpan time)
        {
            if (_keyframes.Count == 0) return default!;
            if (_keyframes.Count == 1) return _keyframes[0].Value;

            if (time <= _keyframes[0].Start) return _keyframes[0].Value;
            if (time >= _keyframes[^1].Start) return _keyframes[^1].Value;

            for (int i = 0; i < _keyframes.Count - 1; i++)
            {
                Keyframe<T> from = _keyframes[i];
                Keyframe<T> to = _keyframes[i + 1];

                //a time exactly on a keyframe belongs to the segment it starts, so a Hold changes at its own time
                if (time < from.Start || time >= to.Start) continue;

                return InterpolateSegment(from, to, time);
            }

            return _keyframes[^1].Value;
        }

        /// <summary>The value between two neighbouring keyframes.</summary>
        /// <param name="from">The earlier keyframe.</param>
        /// <param name="to">The later keyframe.</param>
        /// <param name="time">A content time between them.</param>
        /// <returns>The value.</returns>
        protected virtual T InterpolateSegment(Keyframe<T> from, Keyframe<T> to, TimeSpan time)
        {
            //Hold on the leaving side wins, whatever the arriving side says
            if (from.OutInterpolation == InterpolationType.Hold) return from.Value;

            float u = SolveBezierU(time, from, to);
            return EvaluateValueCubic(from, to, u);
        }

        //the Bezier parameter u in [0, 1] whose time is `time`, by bisection. A Linear side's control point sits
        //on its keyframe. The time curve is monotonic because handles never reach past a neighbour
        private static float SolveBezierU<TVal>(TimeSpan time, Keyframe<TVal> from, Keyframe<TVal> to)
        {
            double tA = from.Start.Ticks;
            double tB = to.Start.Ticks;
            double target = time.Ticks;

            double p1 = from.OutInterpolation == InterpolationType.Bezier && from.OutHandle != null
                ? tA + from.OutHandle.TimeOffset.Ticks
                : tA;

            double p2 = to.InInterpolation == InterpolationType.Bezier && to.InHandle != null
                ? tB - to.InHandle.TimeOffset.Ticks
                : tB;

            double lo = 0, hi = 1;
            for (int i = 0; i < 40; i++)
            {
                double mid = (lo + hi) / 2;
                double m = 1 - mid;
                double sampled =
                    (m * m * m * tA) + (3 * m * m * mid * p1) + (3 * m * mid * mid * p2) + (mid * mid * mid * tB);

                if (sampled < target) lo = mid; else hi = mid;
            }

            return (float)((lo + hi) / 2);
        }

        private T EvaluateValueCubic(Keyframe<T> from, Keyframe<T> to, float u)
        {
            T p1 = from.OutInterpolation == InterpolationType.Bezier && from.OutHandle != null
                ? _interpolator.Add(from.Value, from.OutHandle.ValueOffset)
                : from.Value;

            T p2 = to.InInterpolation == InterpolationType.Bezier && to.InHandle != null
                ? _interpolator.Add(to.Value, to.InHandle.ValueOffset)
                : to.Value;

            float m = 1 - u;

            T term0 = _interpolator.Scale(from.Value, m * m * m);
            T term1 = _interpolator.Scale(p1, 3 * m * m * u);
            T term2 = _interpolator.Scale(p2, 3 * m * u * u);
            T term3 = _interpolator.Scale(to.Value, u * u * u);

            return _interpolator.Add(_interpolator.Add(term0, term1), _interpolator.Add(term2, term3));
        }

        /// <summary>How far through a segment a time is, in the curve's own parameter, taking the time handles into account.</summary>
        /// <param name="time">A content time between the keyframes.</param>
        /// <param name="from">The earlier keyframe.</param>
        /// <param name="to">The later keyframe.</param>
        /// <returns>The parameter, from 0 at <paramref name="from"/> to 1 at <paramref name="to"/>.</returns>
        protected static float SolveU(TimeSpan time, Keyframe<T> from, Keyframe<T> to) =>
            SolveBezierU(time, from, to);
    }
}