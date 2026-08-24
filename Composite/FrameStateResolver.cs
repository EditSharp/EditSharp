using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components;
using EditSharp.Components.Clips;
using EditSharp.Components.Transitions;

namespace EditSharp.Composite
{
    /// <summary>
    /// Answers "what does the timeline look like at output frame N" — every
    /// visible clip on every VideoChannel, each carrying only its
    /// clip-relative time; any transition already reduced to a progress
    /// value.
    ///
    /// This is where time leaves the pipeline. Downstream of here nothing
    /// knows about clip start times or transition durations; SkFrameCompositor
    /// and, beneath it, SkClipContentSource/EffectGraphEvaluatorSk get
    /// literals only.
    ///
    /// Only VideoChannels are resolved into FrameChannels here — an
    /// AudioChannel has no BlendMode and its clips (AudioClip) have no visual
    /// graph to draw, so it simply contributes nothing to a video frame (its
    /// audio is mixed separately, in AudioMixer).
    ///
    /// REWRITE ("channels split by kind"): iterates timeline.VideoChannels
    /// directly now, rather than timeline.Channels filtered by `is
    /// VideoChannel` — Timeline keeps VideoChannel and AudioChannel as two
    /// separate lists (see Timeline.cs's own remarks), so there's no longer
    /// a mixed list to filter here, and this is called once per rendered
    /// frame, so skipping the type check and the allocation the mixed
    /// Channels view would otherwise cost here is worth doing.
    ///
    /// "CLIPS ARE GRAPHS" REWRITE: this resolver no longer needs a
    /// per-clip native-size dictionary at all — see FrameFilterChain.cs's own
    /// remarks on why FrameClip dropped Transform/NativeWidth/NativeHeight.
    /// Resolve's signature shrank to match: just the timeline, frame index,
    /// and fps.
    /// </summary>
    internal static class FrameStateResolver
    {
        public static FrameState Resolve(Timeline timeline, int frameIndex, int fps)
        {
            TimeSpan time = TimeSpan.FromSeconds(frameIndex / (double)fps);

            var channels = new List<FrameChannel>();

            foreach (VideoChannel videoChannel in timeline.VideoChannels)
            {
                FrameChannel? resolved = ResolveChannel(videoChannel, time);
                if (resolved != null) channels.Add(resolved);
            }

            return new FrameState { FrameIndex = frameIndex, Channels = channels };
        }

        private static FrameChannel? ResolveChannel(VideoChannel channel, TimeSpan time)
        {
            //clips on a channel cannot overlap, so at most one is live at any
            //instant — the second entry, when there is one, comes from a
            //transition reaching back into the clip before it
            Clip? active = channel.Clips.FirstOrDefault(
                c => time >= c.Start && time < c.End);

            if (active == null) return null;

            var clips = new List<FrameClip> { BuildFrameClip(active, time) };

            var (transition, progress, outgoing) = ResolveTransition(channel, active, time);

            if (transition is not null && outgoing != null)
            {
                //xfade-equivalent takes the OUTGOING clip first, so the
                //incoming clip resolved above moves into second place
                clips.Insert(0, BuildFrameClip(outgoing, time));
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
        /// A transition belongs to the clip it transitions OUT of
        /// (Transition.From), and only counts when the two clips actually
        /// touch — a transition declared across a gap is ignored rather than
        /// stretching either clip to close it.
        ///
        /// The transition occupies the START of the incoming clip: neither
        /// clip loses any time to it, and the incoming clip begins real
        /// playback once the transition ends.
        /// </summary>
        private static (Transition? Transition, double Progress, Clip? Outgoing) ResolveTransition(
            VideoChannel channel, Clip active, TimeSpan time)
        {
            Clip? previous = channel.Clips
                .Where(c => c.End == active.Start)
                .FirstOrDefault();

            if (previous == null) return (null, 0, null);

            Transition? transition = channel.Transitions
                .FirstOrDefault(t => ReferenceEquals(t.From, previous));

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

        private static FrameClip BuildFrameClip(Clip clip, TimeSpan time)
        {
            double clipSeconds = (time - clip.Start).TotalSeconds;

            return new FrameClip
            {
                Clip = clip,
                ClipSeconds = clipSeconds,
            };
        }
    }
}