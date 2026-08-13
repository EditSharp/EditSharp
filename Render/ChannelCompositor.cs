using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components;
using EditSharp.Components.Clips;
using EditSharp.Components.Effects;

namespace EditSharp.Assembly
{
    /// <summary>
    /// Composites one channel's clips onto the running timeline accumulator.
    ///
    /// A channel is NOT materialized as its own full-length canvas stream. Clips
    /// within a channel cannot overlap, so each one is drawn straight onto the
    /// accumulator inside its own `enable` window and costs only its own duration —
    /// a channel holding four one-second clips across a five minute timeline pays
    /// for four seconds of compositing, not five minutes.
    ///
    /// The exception is a transition, which needs both clips on screen at once.
    /// Adjacent clips joined by a transition are chained into a single SEGMENT
    /// first, and the segment is what gets composited. A channel with no
    /// transitions is therefore one segment per clip.
    /// </summary>
    internal static class ChannelCompositor
    {
        /// <summary>
        /// Draws every clip in the channel onto <paramref name="accumulator"/> and
        /// returns the new accumulator label. Clips that contribute no picture
        /// (audio-only sources) are skipped entirely rather than composited as
        /// transparent frames.
        /// </summary>
        public static string Compose(
            Channel channel, string accumulator,
            IReadOnlyDictionary<Clip, ClipContent> contents,
            InputGraph graph, int canvasWidth, int canvasHeight, int fps,
            ConcurrentBag<string> tempFiles)
        {
            string current = accumulator;

            foreach (List<Clip> segment in BuildSegments(channel))
            {
                TransformExpressions.WorkRect rect = SegmentWorkRect(
                    channel, segment, contents, canvasWidth, canvasHeight, fps);

                string? segmentLabel = ComposeSegment(
                    channel, segment, contents, graph, canvasWidth, canvasHeight, fps,
                    tempFiles, rect);

                if (segmentLabel == null) continue;

                current = Draw(
                    current, segmentLabel, segment[0].Start, segment[^1].End,
                    channel.BlendMode, graph, canvasWidth, canvasHeight, fps, rect);
            }

            return current;
        }

        /// <summary>
        /// Groups the channel's clips into runs joined by transitions.
        ///
        /// A transition is only meaningful between clips that actually touch — a
        /// crossfade needs the incoming clip to start where the outgoing one ends.
        /// A transition declared across a gap is ignored rather than silently
        /// stretching either clip to close it.
        /// </summary>
        public static List<List<Clip>> BuildSegments(Channel channel)
        {
            var ordered = channel.Clips.Values.OrderBy(c => c.Start).ToList();
            var segments = new List<List<Clip>>();

            List<Clip>? currentSegment = null;

            for (int i = 0; i < ordered.Count; i++)
            {
                Clip clip = ordered[i];

                currentSegment ??= [];
                currentSegment.Add(clip);

                bool joinsNext =
                    i + 1 < ordered.Count &&
                    ordered[i + 1].Start == clip.End &&
                    FindTransition(channel, clip) != null;

                if (!joinsNext)
                {
                    segments.Add(currentSegment);
                    currentSegment = null;
                }
            }

            return segments;
        }

        /// <summary>
        /// The frame every clip in this segment is rendered in.
        ///
        /// ONE rect for the whole segment, not one per clip: a segment exists
        /// precisely because its clips are joined by transitions, and `xfade` (and
        /// `concat`, on the degenerate path) require their inputs to have matching
        /// dimensions. So the per-clip boxes are unioned and every clip in the
        /// segment renders into the result.
        ///
        /// Falls back to the full canvas — which is exactly the pre-bounding-box
        /// behaviour — in the two cases where a smaller frame would change what
        /// gets drawn rather than just where it is computed. Both are deliberate
        /// conservatism: they cost the optimization on those clips and nothing
        /// else.
        /// </summary>
        private static TransformExpressions.WorkRect SegmentWorkRect(
            Channel channel, List<Clip> segment,
            IReadOnlyDictionary<Clip, ClipContent> contents,
            int canvasWidth, int canvasHeight, int fps)
        {
            var full = TransformExpressions.WorkRect.FullCanvas(canvasWidth, canvasHeight);

            //A non-Normal blend mode feeds the segment and the canvas-sized
            //accumulator into `blend`, which needs both at the same size. Cropping
            //the accumulator to the segment's rect would work and would be faster
            //still, but it is a second geometry change riding on this one — left
            //for later rather than bundled in.
            if (channel.BlendMode != BlendMode.Normal) return full;

            TransformExpressions.WorkRect? union = null;

            foreach (Clip clip in segment)
            {
                if (!contents.TryGetValue(clip, out ClipContent content)) return full;
                if (content.VideoLabel == null) continue;
                if (RequiresFullCanvas(clip)) return full;

                TransformExpressions.WorkRect rect = TransformExpressions.ComputeWorkRect(
                    clip, content.NativeWidth, content.NativeHeight,
                    canvasWidth, canvasHeight, fps, clip.Duration.TotalSeconds,
                    PostTransformMargin(clip, canvasWidth, canvasHeight));

                union = union == null ? rect : union.Value.Union(rect);
            }

            return union ?? full;
        }

        /// <summary>
        /// Effects whose output extends past the clip's own content in a way the
        /// bounding box does not model, so the clip keeps a full-canvas frame.
        ///
        /// A PRE-transform blur or drop shadow spreads the content's silhouette
        /// outward inside the clip's own frame, BEFORE the warp — so the extra
        /// reach is in model space and gets projected along with everything else,
        /// rather than being a fixed number of canvas pixels the box could simply
        /// be grown by. A POST-transform RoundedCornersEffect masks the frame's own
        /// rectangle (already documented as only meaningful on unwarped content),
        /// and shrinking the frame would change which rectangle that is.
        /// </summary>
        private static bool RequiresFullCanvas(Clip clip) =>
            clip.Effects.Any(e => e.Enabled && (
                (e.Stage == EffectStage.PreTransform && e is BlurEffect or DropShadowEffect) ||
                (e.Stage == EffectStage.PostTransform && e is RoundedCornersEffect)));

        /// <summary>
        /// How far past its content a clip's POST-transform effects draw, in canvas
        /// pixels. These run in screen space after the warp, so their reach is a
        /// plain number of pixels and the box can just be grown by it.
        ///
        /// The margins mirror what the effects themselves compute: 3 sigma covers
        /// effectively all of a Gaussian, and the shadow additionally shifts by its
        /// offset. Sigma is clamped the same way ClipEffects clamps it, so a
        /// user-set radius cannot make the box disagree with the buffer.
        /// </summary>
        private static int PostTransformMargin(Clip clip, int canvasWidth, int canvasHeight)
        {
            int margin = 0;

            foreach (Effect effect in clip.Effects)
            {
                if (!effect.Enabled || effect.Stage != EffectStage.PostTransform) continue;

                switch (effect)
                {
                    case DropShadowEffect shadow:
                    {
                        double sigma = Math.Clamp(shadow.BlurRadius * canvasWidth, 0.1, 1024.0);
                        int offsetX = (int)Math.Round(Math.Abs(shadow.Offset.X) * canvasWidth / 2.0);
                        int offsetY = (int)Math.Round(Math.Abs(shadow.Offset.Y) * canvasHeight / 2.0);

                        margin = Math.Max(
                            margin,
                            (int)Math.Ceiling(sigma * 3) + Math.Max(offsetX, offsetY));
                        break;
                    }

                    case BlurEffect blur:
                    {
                        double sigma = Math.Clamp(blur.Radius * canvasWidth, 0.1, 1024.0);
                        margin = Math.Max(margin, (int)Math.Ceiling(sigma * 3));
                        break;
                    }
                }
            }

            return margin;
        }

        public static Transition? FindTransition(Channel channel, Clip clip) =>
            channel.Transitions.FirstOrDefault(t => ReferenceEquals(t.Item1, clip)).Item2;

        /// <summary>
        /// Builds one segment's video: each clip through the content builder and
        /// clip chain, then chained together by any transitions between them.
        /// Returns null when nothing in the segment draws anything.
        /// </summary>
        private static string? ComposeSegment(
            Channel channel, List<Clip> segment,
            IReadOnlyDictionary<Clip, ClipContent> contents,
            InputGraph graph, int canvasWidth, int canvasHeight, int fps,
            ConcurrentBag<string> tempFiles,
            TransformExpressions.WorkRect rect)
        {
            string? current = null;
            double currentSeconds = 0;

            for (int i = 0; i < segment.Count; i++)
            {
                Clip clip = segment[i];

                ClipContent content = contents[clip];
                if (content.VideoLabel == null) continue;

                string built = ClipVideoChain.Build(
                    clip, content.VideoLabel,
                    content.NativeWidth, content.NativeHeight,
                    canvasWidth, canvasHeight, fps, clip.Duration.TotalSeconds,
                    graph, tempFiles, content.ModulateAlreadyApplied, rect);

                if (current == null)
                {
                    current = built;
                    currentSeconds = clip.Duration.TotalSeconds;
                    continue;
                }

                Transition? transition = FindTransition(channel, segment[i - 1]);
                double transitionSeconds = transition == null
                    ? 0
                    : GraphUtilities.Clamp(
                        transition.Duration.TotalSeconds, 0.05,
                        Math.Min(currentSeconds, clip.Duration.TotalSeconds) - 0.05);

                current = Chain(current, built, currentSeconds, transitionSeconds, transition, graph);
                currentSeconds += clip.Duration.TotalSeconds;
            }

            return current;
        }

        /// <summary>
        /// Joins two clips with a transition, without either of them losing any
        /// time.
        ///
        /// xfade normally consumes its overlap from the two inputs, so a plain
        /// crossfade would pull everything after it earlier and quietly desynchronize
        /// the channel from its neighbours. Prepending the incoming clip with a
        /// frozen copy of its own first frame, exactly as long as the transition,
        /// gives xfade the overlap it needs out of material that did not exist
        /// before — so the joined length stays the sum of the two clips, and the
        /// incoming clip begins real playback precisely when the transition ends.
        /// Verified against the frame numbering directly.
        ///
        /// settb=AVTB on both sides because xfade requires a matching timebase, and
        /// source files can carry unusual native ones that survive every other
        /// normalization.
        /// </summary>
        private static string Chain(
            string outgoing, string incoming,
            double outgoingSeconds, double transitionSeconds,
            Transition? transition, InputGraph graph)
        {
            string joined = graph.NextLabel("chjoin");

            if (transition == null || transitionSeconds <= 0)
            {
                graph.FilterLines.Add(
                    $"[{outgoing}][{incoming}]concat=n=2:v=1:a=0[{joined}]");

                return joined;
            }

            string name = Constants.XfadeNames.TryGetValue(transition.Type, out string? mapped)
                ? mapped
                : "fade";

            string outgoingTb = graph.NextLabel("chtba");
            graph.FilterLines.Add($"[{outgoing}]settb=AVTB[{outgoingTb}]");

            string frozen = graph.NextLabel("chfreeze");
            graph.FilterLines.Add(
                $"[{incoming}]tpad=start_mode=clone:" +
                $"start_duration={GraphUtilities.Num(transitionSeconds)},settb=AVTB[{frozen}]");

            graph.FilterLines.Add(
                $"[{outgoingTb}][{frozen}]xfade=transition={name}:" +
                $"duration={GraphUtilities.Num(transitionSeconds)}:" +
                $"offset={GraphUtilities.Num(outgoingSeconds - transitionSeconds)}[{joined}]");

            return joined;
        }

        /// <summary>
        /// Draws a finished segment onto the accumulator at its timeline position.
        ///
        /// The segment's own stream starts at zero, so it is shifted to its start
        /// time and gated with `enable` so it only paints inside its own window.
        /// repeatlast=0 stops the last frame being held over the rest of the
        /// timeline once the segment ends.
        /// </summary>
        private static string Draw(
            string accumulator, string segment,
            TimeSpan start, TimeSpan end,
            BlendMode blendMode,
            InputGraph graph, int canvasWidth, int canvasHeight, int fps,
            TransformExpressions.WorkRect rect)
        {
            string shifted = graph.NextLabel("chshift");
            graph.FilterLines.Add(
                $"[{segment}]setpts=PTS-STARTPTS+{GraphUtilities.Num(start.TotalSeconds)}/TB[{shifted}]");

            string window =
                $"enable='between(t,{GraphUtilities.Num(start.TotalSeconds)}," +
                $"{GraphUtilities.Num(end.TotalSeconds)})'";

            if (blendMode == BlendMode.Normal)
            {
                string plain = graph.NextLabel("chdraw");
                graph.FilterLines.Add(
                    $"[{accumulator}][{shifted}]overlay={rect.X}:{rect.Y}:" +
                    $"format=auto:repeatlast=0:{window}[{plain}]");

                return plain;
            }

            if (!Constants.BlendModeNames.TryGetValue(blendMode, out string? mode))
                throw new NotSupportedException(
                    $"Channel blend mode {blendMode} has no ffmpeg equivalent.");

            // A blend mode has to apply only where the segment actually covers the
            // accumulator. `blend` ignores alpha and would recolour the whole frame,
            // so the blended result is masked back down by the segment's own alpha
            // and then overlaid normally — outside the segment's shape the
            // accumulator is left exactly as it was.
            //everything below works on canvas-sized streams. That is safe because
            //SegmentWorkRect returns the full canvas for any channel whose blend
            //mode is not Normal, so `rect` here is always (0, 0, canvas) — the
            //overlay at the end stays at 0:0 for that reason rather than by
            //accident.
            string blendSource = graph.NextLabel("chblendsrc");
            string maskSource = graph.NextLabel("chblendmask");
            graph.FilterLines.Add($"[{shifted}]split=2[{blendSource}][{maskSource}]");

            string mask = graph.NextLabel("chblendalpha");
            graph.FilterLines.Add($"[{maskSource}]format=rgba,alphaextract[{mask}]");

            string backdropCopy = graph.NextLabel("chblendbase");
            string backdropKeep = graph.NextLabel("chblendkeep");
            graph.FilterLines.Add($"[{accumulator}]split=2[{backdropCopy}][{backdropKeep}]");

            string blended = graph.NextLabel("chblended");
            graph.FilterLines.Add(
                $"[{backdropCopy}][{blendSource}]blend=all_mode={mode}:shortest=0[{blended}]");

            string masked = graph.NextLabel("chblendmasked");
            graph.FilterLines.Add($"[{blended}][{mask}]alphamerge[{masked}]");

            string next = graph.NextLabel("chdraw");
            graph.FilterLines.Add(
                $"[{backdropKeep}][{masked}]overlay=0:0:format=auto:repeatlast=0:{window}[{next}]");

            return next;
        }
    }
}
