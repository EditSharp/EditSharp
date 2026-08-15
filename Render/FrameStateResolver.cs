using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components;
using EditSharp.Components.Clips;

namespace EditSharp.Render
{
    /// <summary>
    /// Answers "what does the timeline look like at output frame N" — every
    /// visible clip, each with its transform already evaluated, and any
    /// transition already reduced to a progress value.
    ///
    /// This is where time leaves the pipeline. Downstream of here nothing
    /// knows about keyframes, easing curves, clip start times or transition
    /// durations; the filter chain gets literals only.
    /// </summary>
    internal static class FrameStateResolver
    {
        public static FrameState Resolve(
            Timeline timeline, int frameIndex, int fps,
            IReadOnlyDictionary<Clip, OptimizedMediaBuilder.OptimizedMedia> optimizedMedia,
            IReadOnlyDictionary<Clip, (int Width, int Height)> nativeSizes,
            IReadOnlyDictionary<Clip, string> staticImages)
        {
            TimeSpan time = TimeSpan.FromSeconds(frameIndex / (double)fps);

            var channels = new List<FrameChannel>();

            foreach (Channel channel in timeline.Channels)
            {
                FrameChannel? resolved = ResolveChannel(
                    channel, time, optimizedMedia, nativeSizes, staticImages);

                if (resolved != null) channels.Add(resolved);
            }

            return new FrameState { FrameIndex = frameIndex, Channels = channels };
        }

        private static FrameChannel? ResolveChannel(
            Channel channel, TimeSpan time,
            IReadOnlyDictionary<Clip, OptimizedMediaBuilder.OptimizedMedia> optimizedMedia,
            IReadOnlyDictionary<Clip, (int Width, int Height)> nativeSizes,
            IReadOnlyDictionary<Clip, string> staticImages)
        {
            //clips on a channel cannot overlap, so at most one is live at any
            //instant — the second entry, when there is one, comes from a
            //transition reaching back into the clip before it
            Clip? active = channel.Clips.Values.FirstOrDefault(
                c => time >= c.Start && time < c.End);

            if (active == null) return null;

            var clips = new List<FrameClip>
            {
                BuildFrameClip(active, time, optimizedMedia, nativeSizes, staticImages),
            };

            var (transition, progress, outgoing) = ResolveTransition(channel, active, time);

            if (transition != null && outgoing != null)
            {
                //xfade takes the OUTGOING clip first, so the incoming clip that
                //was resolved above moves into second place
                clips.Insert(0, BuildFrameClip(
                    outgoing, time, optimizedMedia, nativeSizes, staticImages));
            }

            return new FrameChannel
            {
                BlendMode = channel.BlendMode,
                Clips = clips,
                Transition = transition?.Type,
                TransitionProgress = progress,
            };
        }

        /// <summary>
        /// Whether a transition is mid-flight at this instant, and how far
        /// through it is.
        ///
        /// A transition belongs to the clip it transitions OUT of, and only
        /// counts when the two clips actually touch — a transition declared
        /// across a gap is ignored rather than stretching either clip to close it.
        ///
        /// The transition occupies the START of the incoming clip: neither clip
        /// loses any time to it, and the incoming clip begins real playback once
        /// the transition ends.
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
            //joins, with a small floor so a zero/negative duration never divides
            //by zero below
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
            IReadOnlyDictionary<Clip, OptimizedMediaBuilder.OptimizedMedia> optimizedMedia,
            IReadOnlyDictionary<Clip, (int Width, int Height)> nativeSizes,
            IReadOnlyDictionary<Clip, string> staticImages)
        {
            double clipSeconds = (time - clip.Start).TotalSeconds;

            //Clip.TransformAt owns the keyframe/easing rules; resolving them here
            //rather than reimplementing is what keeps the frame-by-frame path
            //and the existing whole-window path producing the same motion
            ClipTransform transform = clip.TransformAt(TimeSpan.FromSeconds(clipSeconds));

            string? sourcePath = null;
            double seek = 0;
            bool isVideoSeek = false;
            bool preTransformEffectsBaked = false;

            //any clip that HAS optimized media reads from it, not just a
            //SourceClip — NoiseClip now pre-renders its `perlin` stream the
            //same way (see OptimizedMediaBuilder.BuildNoiseAsync), since
            //`perlin` is a generator source with no seek and regenerating
            //every preceding frame made the render O(N^2). The dictionary
            //membership IS the condition; there's nothing type-specific left
            //about reading a seekable pre-rendered file
            if (optimizedMedia.TryGetValue(clip, out var media))
            {
                sourcePath = media.Path;
                isVideoSeek = true;
                preTransformEffectsBaked = media.EffectsBaked;

                //This pipeline's current extension behaviour is FREEZE FRAME:
                //a clip whose Duration outlasts its source holds the source's
                //last real frame rather than looping. Clamping the seek to
                //just inside AvailableSeconds is what produces that — without
                //it, a clip running past its source's length would ask the
                //optimized media to seek past its own end.
                seek = Math.Min(clipSeconds, Math.Max(0, media.AvailableSeconds - 0.0005));
            }
            else if (staticImages.TryGetValue(clip, out string? staticPath))
            {
                //an Image SourceClip or a TextClip — no decode, no seek, the
                //same file is read for every frame the clip is visible on
                sourcePath = staticPath;
            }

            var (width, height) = nativeSizes.TryGetValue(clip, out var size)
                ? size
                : (0, 0);

            return new FrameClip
            {
                Clip = clip,
                Transform = transform,
                SourcePath = sourcePath,
                SourceSeekSeconds = seek,
                IsVideoSeek = isVideoSeek,
                NativeWidth = width,
                NativeHeight = height,
                ClipSeconds = clipSeconds,
                PreTransformEffectsBaked = preTransformEffectsBaked,
            };
        }
    }
}
