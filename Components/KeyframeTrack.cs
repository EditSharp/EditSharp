using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.History;

namespace EditSharp.Components
{
    /// <summary>
    /// One end of a keyframe's bezier handle: a 2D offset from the keyframe
    /// itself (time offset + value offset), not a single scalar "how much
    /// speed to remove" the way the old EaseInStrength/EaseOutStrength were.
    /// See the migration note on Keyframe.cs's old shape.
    ///
    /// CONVENTION: TimeOffset is always stored as a non-negative magnitude —
    /// "how far this handle reaches toward the OTHER keyframe of the
    /// segment," regardless of whether it's an in-handle or an out-handle.
    /// An out-handle is applied by ADDING TimeOffset to its own keyframe's
    /// Start; an in-handle is applied by SUBTRACTING TimeOffset from its own
    /// keyframe's Start. This is what makes "clamp so it can never cross the
    /// neighboring keyframe" a single one-directional clamp (TimeOffset can
    /// never exceed the gap to the neighbor) instead of needing a sign check
    /// at every call site.
    /// </summary>
    public sealed class KeyframeHandle<T>
    {
        public TimeSpan TimeOffset { get; internal set; }
        public T ValueOffset { get; internal set; }

        internal KeyframeHandle(TimeSpan timeOffset, T valueOffset)
        {
            TimeOffset = timeOffset;
            ValueOffset = valueOffset;
        }
    }

    public class Keyframe<T> : IKeyframe
    {
        object? IKeyframe.Value => Value;

        //relative to the clip/track's own beginning — see the "Keyframe
        //anchoring under Trim" section of the schema doc for why Trim never
        //has to touch this
        TimeSpan _start;
        public TimeSpan Start { get => _start; internal set => Transaction.Set(this, ref _start, value, static (o, v) => o._start = v); }

        T _value;
        public T Value { get => _value; set => Transaction.Set(this, ref _value, value, static (o, v) => o._value = v); }

        //shape of curve ARRIVING at this keyframe / LEAVING this keyframe.
        //Independent per side — see KeyframeTrack.Evaluate for how a
        //segment's shape is jointly decided by the left keyframe's
        //OutInterpolation and the right keyframe's InInterpolation.
        InterpolationType _inInterpolation = InterpolationType.Linear;
        public InterpolationType InInterpolation { get => _inInterpolation; set => Transaction.Set(this, ref _inInterpolation, value, static (o, v) => o._inInterpolation = v); }
        InterpolationType _outInterpolation = InterpolationType.Linear;
        public InterpolationType OutInterpolation { get => _outInterpolation; set => Transaction.Set(this, ref _outInterpolation, value, static (o, v) => o._outInterpolation = v); }

        //meaningful only when the matching Interpolation is Bezier
        KeyframeHandle<T>? _inHandle;
        public KeyframeHandle<T>? InHandle { get => _inHandle; internal set => Transaction.Set(this, ref _inHandle, value, static (o, v) => o._inHandle = v); }
        KeyframeHandle<T>? _outHandle;
        public KeyframeHandle<T>? OutHandle { get => _outHandle; internal set => Transaction.Set(this, ref _outHandle, value, static (o, v) => o._outHandle = v); }

        TangentMode _inTangentMode = TangentMode.Auto;
        public TangentMode InTangentMode { get => _inTangentMode; internal set => Transaction.Set(this, ref _inTangentMode, value, static (o, v) => o._inTangentMode = v); }
        TangentMode _outTangentMode = TangentMode.Auto;
        public TangentMode OutTangentMode { get => _outTangentMode; internal set => Transaction.Set(this, ref _outTangentMode, value, static (o, v) => o._outTangentMode = v); }

        internal Keyframe(TimeSpan start, T value)
        {
            _start = start;
            _value = value;
        }
    }

    /// <summary>
    /// One animatable property's own timeline of keyframes. See the schema
    /// doc's Keyframes section for the full design — this is "one track per
    /// animatable property," replacing the old whole-ClipTransform-per-
    /// keyframe list.
    ///
    /// Encapsulation principle applies here same as everywhere else in the
    /// schema: Keyframes is read-only externally, all mutation goes through
    /// this class's own methods — which is what makes handle-clamping and
    /// Auto-tangent-recompute-on-neighbor-change actually enforceable rather
    /// than something a caller could bypass by editing a Keyframe directly.
    /// </summary>
    public class KeyframeTrack<T>
    {
        private readonly IInterpolator<T> _interpolator;
        private readonly List<Keyframe<T>> _keyframes = [];

        public IReadOnlyList<Keyframe<T>> Keyframes => _keyframes;

        public KeyframeTrack() : this(Interpolators.Resolve<T>()) { }

        public KeyframeTrack(IInterpolator<T> interpolator)
        {
            _interpolator = interpolator ?? throw new ArgumentNullException(nameof(interpolator));
        }

        /// <summary>
        /// Adding at a Start that already has a keyframe updates that
        /// keyframe's Value in place rather than creating a duplicate entry —
        /// no two keyframes on one track can share a Start, and this is the
        /// least surprising way to enforce it (matches "add a keyframe at
        /// the current time" in every mainstream tool when one's already
        /// there).
        /// </summary>
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

            //FOUND IN THE FIELD, FIXED: this used to only recompute the
            //newly-inserted keyframe's own Auto handles. But a keyframe's
            //Auto handle depends on its NEIGHBORS (RecomputeAutoHandles
            //walks to prev/next) — inserting a new keyframe changes what
            //"neighbor" means for whichever keyframe(s) used to be adjacent
            //to this Start. Left unrefreshed, an edge keyframe added alone
            //(no neighbors yet) computes no handles at all and never gets a
            //second chance once a real neighbor shows up later — its
            //OutHandle/InHandle stays null forever, so later marking that
            //side Bezier has no effect (EvaluateValueCubic's Bezier branch
            //requires a non-null handle) and the segment silently renders
            //as if it were still Linear. RemoveKeyframe/MoveKeyframe already
            //refresh both neighbors on their own operations; AddKeyframe
            //needs the same treatment.
            int index = _keyframes.IndexOf(created);
            RecomputeAutoHandles(created);
            if (index > 0) RecomputeAutoHandles(_keyframes[index - 1]);
            if (index < _keyframes.Count - 1) RecomputeAutoHandles(_keyframes[index + 1]);

            return created;
        }

        /// <summary>
        /// Factory hook so a subclass (PositionTrack) can hand back its own
        /// Keyframe subtype (SpatialKeyframe) instead of a plain Keyframe&lt;T&gt;,
        /// without AddKeyframe itself needing to know about that.
        /// </summary>
        protected virtual Keyframe<T> CreateKeyframe(TimeSpan start, T value) => new(start, value);

        /// <summary>
        /// Moves every keyframe by `amount`, in place. Handles are offsets
        /// from their own keyframe, so they come along untouched. Keyframes
        /// may end up before zero: a head trim leaves the ones it cut past
        /// at negative times, so an extend back restores them exactly.
        /// </summary>
        public void Shift(TimeSpan amount)
        {
            if (amount == TimeSpan.Zero) return;

            foreach (Keyframe<T> keyframe in _keyframes) keyframe.Start += amount;
        }

        /// <summary>Deep copy, of the same concrete track type — see CreateEmptyCopy/CopyKeyframeExtras.</summary>
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

                //SetHandle promotes Auto -> Free as a side effect (see its
                //own remarks) — restore the source's real tangent modes now
                //that both handles are copied
                added.InTangentMode = kf.InTangentMode;
                added.OutTangentMode = kf.OutTangentMode;

                CopyKeyframeExtras(kf, added);
            }

            return copy;
        }

        /// <summary>An empty track of this exact type, for Duplicate — a subclass returns its own type.</summary>
        protected virtual KeyframeTrack<T> CreateEmptyCopy() => new(_interpolator);

        /// <summary>Copies whatever a keyframe subtype carries beyond the base fields — see PositionTrack.</summary>
        protected virtual void CopyKeyframeExtras(Keyframe<T> source, Keyframe<T> target) { }

        /// <summary>
        /// A track reduced to zero keyframes is a valid state, not an error —
        /// Animatable&lt;T&gt;.Evaluate falls back to StaticValue whenever there
        /// are fewer than 2 (0 or 1 both qualify).
        /// </summary>
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
                //the removed keyframe's neighbors are now adjacent to each
                //other — an Auto handle on either needs to be recomputed
                //against its new neighbor
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

        /// <summary>
        /// Clamps newStart so the keyframe can never move onto or past a
        /// neighboring keyframe on the same track — same clamping principle
        /// already applied to a handle's own TimeOffset, just applied to the
        /// keyframe's own position now too.
        /// </summary>
        public void MoveKeyframe(Keyframe<T> keyframe, TimeSpan newStart, T newValue)
        {
            int index = _keyframes.IndexOf(keyframe);
            if (index < 0) return;

            TimeSpan floor = index > 0 ? _keyframes[index - 1].Start : TimeSpan.MinValue;
            TimeSpan ceiling = index < _keyframes.Count - 1 ? _keyframes[index + 1].Start : TimeSpan.MaxValue;

            //strictly between neighbors — touching either would collide with
            //the "no two keyframes share a Start" rule
            TimeSpan clampedFloor = floor == TimeSpan.MinValue ? floor : floor + TimeSpan.FromTicks(1);
            TimeSpan clampedCeiling = ceiling == TimeSpan.MaxValue ? ceiling : ceiling - TimeSpan.FromTicks(1);

            TimeSpan clamped = newStart < clampedFloor ? clampedFloor
                : newStart > clampedCeiling ? clampedCeiling
                : newStart;

            keyframe.Start = clamped;
            keyframe.Value = newValue;

            //re-sort in place — a clamp keeps it between neighbors, but a
            //caller could still hand this the keyframe list out of the order
            //it was enumerated in
            List<Keyframe<T>> before = [.. _keyframes];
            Transaction.Apply(
                () => _keyframes.Sort((a, b) => a.Start.CompareTo(b.Start)),
                () => { _keyframes.Clear(); _keyframes.AddRange(before); },
                "reorder keyframes");

            //FOUND IN THE FIELD: this used to only ever recompute the LEFT
            //neighbor's Auto handle (`index - 1`, using the PRE-move index)
            //— the right neighbor's Auto handle depends on the gap to this
            //keyframe too and was left stale after a move, unlike
            //RemoveKeyframe, which correctly refreshes both sides. Recompute
            //using the keyframe's freshly-resorted index so both actual
            //neighbors (left AND right) get refreshed.
            int newIndex = _keyframes.IndexOf(keyframe);
            RecomputeAutoHandles(keyframe);
            if (newIndex > 0) RecomputeAutoHandles(_keyframes[newIndex - 1]);
            if (newIndex < _keyframes.Count - 1) RecomputeAutoHandles(_keyframes[newIndex + 1]);
        }

        /// <summary>
        /// Sets a handle's offset, clamping TimeOffset so it can never cross
        /// the neighboring keyframe on that side. Setting a handle on a
        /// keyframe whose TangentMode for that side is Auto switches it to
        /// Free first — matches After-Effects-style "manually dragging an
        /// Auto handle promotes it to Free."
        ///
        /// Smooth mirrors the OTHER side's handle through the keyframe
        /// automatically: setting one side of a Smooth-paired keyframe
        /// updates the mirrored side too, so the curve stays continuous
        /// without the caller having to set both by hand.
        /// </summary>
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

        /// <summary>
        /// Recomputes an Auto handle from neighboring keyframes — a simple,
        /// symmetric "continuous through the neighbors" heuristic (the
        /// tangent direction is the direction from the previous keyframe to
        /// the next one), reasoned from Auto's job description rather than
        /// matched pixel-for-pixel against any specific NLE's own Auto
        /// algorithm. Both handle sides are recomputed together so an Auto
        /// keyframe always looks continuous on both sides at once.
        /// </summary>
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

            //One-sided span for an edge keyframe (only one real neighbor)
            //uses the standard one-sided-tangent proportion instead of the
            //symmetric /6 — see the FOUND IN THE FIELD note below for why
            //this alone isn't sufficient.
            TimeSpan span = hasPrev && hasNext
                ? TimeSpan.FromTicks((next.Start - prev.Start).Ticks / 6)
                : hasNext
                    ? TimeSpan.FromTicks((next.Start - keyframe.Start).Ticks / 3)
                    : TimeSpan.FromTicks((keyframe.Start - prev.Start).Ticks / 3);

            //FOUND IN THE FIELD, FIXED: `span` is derived from the WHOLE
            //prev-to-next distance (or the one-sided gap), but any single
            //keyframe's actual reach toward ONE neighbor can be shorter than
            //that — e.g. an interior keyframe with a short gap on one side
            //and a long gap on the other, or several keyframes added close
            //together. The old code clamped the handle's TIME component down
            //to that shorter actual gap but left the VALUE component at the
            //full, unclamped tangentValue — a control point that barely
            //advances in time while still carrying (nearly) the whole value
            //delta is a near-vertical approach, which makes the cubic's
            //value component overshoot past the endpoint before snapping
            //back at u=1. Scaling the value component by the exact same
            //ratio the time component got clamped by keeps the handle's
            //slope (value-per-time) consistent instead of steepening it —
            //the standard fix for this class of auto-tangent overshoot
            //(the same reason After Effects' own "Auto Bezier" doesn't
            //overshoot on unevenly-spaced keyframes).
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

        /// <summary>
        /// Fewer than 2 keyframes: Animatable&lt;T&gt; handles the StaticValue
        /// fallback, but Evaluate is still well-defined on its own (holds the
        /// single keyframe's value, or the interpolator's default if empty).
        /// </summary>
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

                //FOUND IN THE FIELD, FIXED: this used to be `time > to.Start`
                //(strict), which meant that at the EXACT instant of an
                //interior keyframe (time == to.Start, and `to` isn't the
                //very last keyframe — that case is already handled by the
                //early return above) THIS segment matched and won, before
                //the next iteration (whose own `from` is this same `to`)
                //ever got a chance to. For a Linear/Bezier segment that's
                //harmless (u solves to 1, so InterpolateSegment already
                //returns `to.Value` either way) — but InterpolateSegment
                //short-circuits a Hold segment straight to `from.Value`
                //regardless of u, so exactly AT a Hold keyframe's own time
                //this returned the OLD held value instead of that
                //keyframe's own new one — one instant too early. Using
                //`>=` here defers time == to.Start to the NEXT segment
                //instead, where `to` is that segment's own `from` and u
                //correctly solves to 0 — giving `to.Value` in every case,
                //matching how the very last keyframe already behaves via
                //the early return above.
                if (time < from.Start || time >= to.Start) continue;

                return InterpolateSegment(from, to, time);
            }

            return _keyframes[^1].Value;
        }

        /// <summary>
        /// Resolves one segment's value at `time`. Virtual so PositionTrack
        /// can reuse the Hold/Linear/Bezier TIMING logic (via the protected
        /// SolveU helper) while swapping in a spatial bezier for the actual
        /// value, instead of this class's plain value-cubic — see the
        /// schema doc's "two-stage, deliberately NOT independent per-axis
        /// easing" remark on PositionTrack.
        /// </summary>
        protected virtual T InterpolateSegment(Keyframe<T> from, Keyframe<T> to, TimeSpan time)
        {
            //Hold on the outgoing side always wins for this segment,
            //regardless of the incoming keyframe's own InInterpolation — a
            //real step function, not "zero easing"
            if (from.OutInterpolation == InterpolationType.Hold) return from.Value;

            float u = SolveBezierU(time, from, to);
            return EvaluateValueCubic(from, to, u);
        }

        /// <summary>
        /// Solves for the cubic-bezier parameter u in [0,1] whose TIME
        /// component equals `time`, by bisection. Deliberately iterative,
        /// unlike the old ffmpeg-era Ease() function's closed-form cubic —
        /// that shortcut only worked because the old model had no real 2D
        /// handles to place in time at all (see Keyframe.cs's migration
        /// note). A Linear side contributes a zero-offset control point
        /// (P1/P2 sitting exactly on their own keyframe), which is why
        /// Linear-in/Bezier-out and similar mixed segments fall out for free
        /// here with no special-casing beyond "is this side Bezier."
        ///
        /// Monotonic and safe to bisect because handle TimeOffsets are
        /// always clamped (see SetHandle/RecomputeAutoHandles) to never
        /// cross the segment's own span.
        /// </summary>
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
                ? _interpolator.Subtract(to.Value, to.InHandle.ValueOffset)
                : to.Value;

            float m = 1 - u;

            T term0 = _interpolator.Scale(from.Value, m * m * m);
            T term1 = _interpolator.Scale(p1, 3 * m * m * u);
            T term2 = _interpolator.Scale(p2, 3 * m * u * u);
            T term3 = _interpolator.Scale(to.Value, u * u * u);

            return _interpolator.Add(_interpolator.Add(term0, term1), _interpolator.Add(term2, term3));
        }

        /// <summary>Shared by PositionTrack's temporal stage — see its own remarks.</summary>
        protected static float SolveU(TimeSpan time, Keyframe<T> from, Keyframe<T> to) =>
            SolveBezierU(time, from, to);
    }
}