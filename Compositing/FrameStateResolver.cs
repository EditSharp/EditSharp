using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components;
using EditSharp.Components.Channels;
using EditSharp.Components.Clips;
using EditSharp.Components.Transitions;
using EditSharp.History;
using EditSharp.Components.Nodes;
using EditSharp.Video;

namespace EditSharp.Compositing
{
    /// <summary>
    /// Answers "what does the timeline look like at output frame N" — every
    /// visible clip on every VideoChannel, each carrying only its
    /// clip-relative time; any transition already reduced to a progress
    /// value.
    ///
    /// This is where time leaves the pipeline. Downstream of here nothing
    /// knows about clip start times or transition durations; FrameCompositor
    /// and, beneath it, ClipContentSource/ImageGraphEvaluator get
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
        /// <summary>
        /// What frame `frameIndex` shows: live clips per channel, transitions,
        /// and a snapshot of each clip's graph. Takes ModelLock's read side, so
        /// the result is safe to compose while the model is edited.
        /// </summary>
        public static FrameState Resolve(Timeline timeline, int frameIndex, int fps)
        {
            using var _ = ModelLock.Read();

            TimeSpan time = TimeOfFrame(frameIndex, fps);

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
            seconds = FfmpegArgs.Clamp(
                seconds, 0.05,
                Math.Max(0.05,
                    Math.Min(previous.Duration.TotalSeconds, active.Duration.TotalSeconds) - 0.05));

            double into = (time - active.Start).TotalSeconds;
            if (into >= seconds) return (null, 0, null);

            return (transition, into / seconds, previous);
        }

        private static FrameClip BuildFrameClip(Clip clip, TimeSpan time)
        {
            //content time: Speed is how fast the clip's graph plays against
            //the timeline, and everything downstream — keyframes, generators,
            //source frame selection, nested timelines — lives in content time
            return new FrameClip
            {
                Clip = clip,
                Graph = clip.Graph.Snapshot(),
                ClipSeconds = ClipSecondsAt(clip, time),
            };
        }

        /// <summary>
        /// The one content-time formula every frame consumer shares — prefetch
        /// buffers match frames by exact content time, so nothing may compute
        /// it any other way.
        /// </summary>
        public static double ClipSecondsAt(Clip clip, TimeSpan time) => (time - clip.Start).TotalSeconds * clip.Speed;

        public static TimeSpan TimeOfFrame(int frameIndex, int fps) => TimeSpan.FromSeconds(frameIndex / (double)fps);
    }
}