using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components;
using EditSharp.Components.Clips;
using EditSharp.Components.Transitions;

namespace EditSharp.Render
{
    /// <summary>
    /// Answers "what does the timeline look like at output frame N" — every
    /// visible clip, each with its transform already evaluated, and any
    /// transition already reduced to a progress value.
    ///
    /// This is where time leaves the pipeline. Downstream of here nothing
    /// knows about keyframes, easing curves, clip start times or transition
    /// durations; SkFrameCompositor gets literals only.
    ///
    /// Item 13 change: no more optimizedMedia/staticImages dictionaries —
    /// those existed to tell the old ffmpeg-per-frame path WHERE a clip's
    /// pixels lived on disk (a seekable file, a static image, nothing).
    /// That's SkClipContentSource's job now, driven by the Clip's own
    /// runtime type rather than precomputed here. All this resolver still
    /// needs from FrameRenderer's upfront prep is each clip's NATIVE PIXEL
    /// SIZE, for aspect-fit math — nativeSizes is unchanged in shape from
    /// before, just narrower in purpose.
    /// </summary>
    internal static class FrameStateResolver
    {
        public static FrameState Resolve(
            Timeline timeline, int frameIndex, int fps,
            IReadOnlyDictionary<Clip, (int Width, int Height)> nativeSizes)
        {
            TimeSpan time = TimeSpan.FromSeconds(frameIndex / (double)fps);

            var channels = new List<FrameChannel>();

            foreach (Channel channel in timeline.Channels)
            {
                FrameChannel? resolved = ResolveChannel(channel, time, nativeSizes);
                if (resolved != null) channels.Add(resolved);
            }

            return new FrameState { FrameIndex = frameIndex, Channels = channels };
        }

        private static FrameChannel? ResolveChannel(
            Channel channel, TimeSpan time,
            IReadOnlyDictionary<Clip, (int Width, int Height)> nativeSizes)
        {
            //clips on a channel cannot overlap, so at most one is live at any
            //instant — the second entry, when there is one, comes from a
            //transition reaching back into the clip before it
            Clip? active = channel.Clips.Values.FirstOrDefault(
                c => time >= c.Start && time < c.End);

            if (active == null) return null;

            var clips = new List<FrameClip> { BuildFrameClip(active, time, nativeSizes) };

            var (transition, progress, outgoing) = ResolveTransition(channel, active, time);

            if (transition is not null && outgoing != null)
            {
                //xfade-equivalent takes the OUTGOING clip first, so the
                //incoming clip resolved above moves into second place
                clips.Insert(0, BuildFrameClip(outgoing, time, nativeSizes));
            }

            return new FrameChannel
            {
                BlendMode = channel.BlendMode,
                Clips = clips,
                Transition = transition,
                TransitionProgress = progress,
            };
        }

        /// <summary>
        /// Whether a transition is mid-flight at this instant, and how far
        /// through it is.
        ///
        /// A transition belongs to the clip it transitions OUT of, and only
        /// counts when the two clips actually touch — a transition declared
        /// across a gap is ignored rather than stretching either clip to
        /// close it.
        ///
        /// The transition occupies the START of the incoming clip: neither
        /// clip loses any time to it, and the incoming clip begins real
        /// playback once the transition ends.
        /// </summary>
        private static (Transition? Transition, double Progress, Clip? Outgoing) ResolveTransition(
            Channel channel, Clip active, TimeSpan time)
        {
            Clip? previous = channel.Clips.Values
                .Where(c => c.End == active.Start)
                .FirstOrDefault();

            if (previous == null) return (null, 0, null);

            Transition? transition = channel.Transitions
                .FirstOrDefault(t => ReferenceEquals(t.Item1, previous)).Item2;

            if (transition == null) return (null, 0, null);

            double seconds = transition.Duration.TotalSeconds;
            if (seconds <= 0) return (null, 0, null);

            //clamped so a transition can never be longer than either clip it
            //joins, with a small floor so a zero/negative duration never
            //divides by zero below
            seconds = GraphUtilities.Clamp(
                seconds, 0.05,
                Math.Max(0.05,
                    Math.Min(previous.Duration.TotalSeconds, active.Duration.TotalSeconds) - 0.05));

            double into = (time - active.Start).TotalSeconds;
            if (into >= seconds) return (null, 0, null);

            return (transition, into / seconds, previous);
        }

        private static FrameClip BuildFrameClip(
            Clip clip, TimeSpan time,
            IReadOnlyDictionary<Clip, (int Width, int Height)> nativeSizes)
        {
            double clipSeconds = (time - clip.Start).TotalSeconds;

            //Clip.TransformAt owns the keyframe/easing rules; resolving them
            //here rather than reimplementing is what keeps this path and
            //the (long-gone) whole-window path producing the same motion
            ClipTransform transform = clip.TransformAt(TimeSpan.FromSeconds(clipSeconds));

            var (width, height) = nativeSizes.TryGetValue(clip, out var size) ? size : (0, 0);

            return new FrameClip
            {
                Clip = clip,
                Transform = transform,
                NativeWidth = width,
                NativeHeight = height,
                ClipSeconds = clipSeconds,
            };
        }
    }
}
