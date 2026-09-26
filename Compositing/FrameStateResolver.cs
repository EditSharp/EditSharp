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
    /// <summary>What the timeline shows at a frame: every live clip on each video channel with its content time, and any transition's progress.</summary>
    /// <remarks>Time stops here: what's downstream gets content times and progress values, never clip starts or transition lengths.</remarks>
    internal static class FrameStateResolver
    {
        /// <summary>
        /// What frame `frameIndex` shows: live clips per channel, transitions,
        /// and a snapshot of each clip's graph. Takes ModelLock's read side, so
        /// the result is safe to compose while the model is edited.
        /// </summary>
        public static FrameState Resolve(Timeline timeline, int frameIndex, Rational fps)
        {
            using var _ = ModelLock.Read();

            Time time = TimeOfFrame(frameIndex, fps);

            var channels = new List<FrameChannel>();

            foreach (VideoChannel videoChannel in timeline.VideoChannels)
            {
                FrameChannel? resolved = ResolveChannel(videoChannel, time);
                if (resolved != null) channels.Add(resolved);
            }

            return new FrameState { FrameIndex = frameIndex, Channels = channels };
        }

        private static FrameChannel? ResolveChannel(VideoChannel channel, Time time)
        {
            //clips on a channel can't overlap, so one is live; a second comes from a transition reaching back into the clip before
            Clip? active = channel.Clips.FirstOrDefault(
                c => time >= c.Start && time < c.End);

            if (active == null) return null;

            var clips = new List<FrameClip> { BuildFrameClip(active, time) };

            var (transition, progress, outgoing) = ResolveTransition(channel, active, time);

            if (transition is not null && outgoing != null)
            {
                //the outgoing clip goes first
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

        //the transition under way at `time`, if any, and how far through. A transition belongs to the clip it
        //leaves (Transition.From), counts only where the two clips touch, and covers the start of the incoming clip
        private static (Transition? Transition, double Progress, Clip? Outgoing) ResolveTransition(
            VideoChannel channel, Clip active, Time time)
        {
            Clip? previous = channel.Clips
                .Where(c => c.End == active.Start)
                .FirstOrDefault();

            if (previous == null) return (null, 0, null);

            Transition? transition = channel.Transitions
                .FirstOrDefault(t => ReferenceEquals(t.From, previous));

            if (transition == null) return (null, 0, null);

            Time length = transition.Duration;
            if (length <= Time.Zero) return (null, 0, null);

            //no longer than either clip it joins, and never zero so the progress can be divided out
            Time margin = Time.FromMilliseconds(50);
            Time longest = Time.Max(margin, Time.Min(previous.Duration, active.Duration) - margin);
            length = Time.Clamp(length, margin, longest);

            Time into = time - active.Start;
            if (into >= length) return (null, 0, null);

            return (transition, into / length, previous);
        }

        private static FrameClip BuildFrameClip(Clip clip, Time time)
        {
            //content time: Speed is how fast the graph plays against the timeline, and everything downstream
            //(keyframes, generators, which source frame) works in content time
            return new FrameClip
            {
                Clip = clip,
                Graph = clip.Graph.Snapshot(),
                ContentTime = clip.ContentTimeAt(time),
            };
        }

        public static Time TimeOfFrame(int frameIndex, Rational fps) => Time.FromFrame(frameIndex, fps);
    }
}