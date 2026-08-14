using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components;
using EditSharp.Components.Clips;

namespace EditSharp.Render
{
    /// <summary>
    /// One clip's contribution to ONE output frame, with every time-varying
    /// value already resolved. This is the whole point of the frame-by-frame
    /// design: EditSharp answers "what does this clip look like right now"
    /// before ffmpeg is invoked, so the filter graph contains literals and no
    /// notion of time at all.
    ///
    /// TransitionProgress/TransitionWith are set only on the OUTGOING side of a
    /// transition that is mid-flight on this frame — see
    /// FrameFilterChainBuilder for how the pair is composited.
    /// </summary>
    internal sealed class FrameClip
    {
        public required Clip Clip { get; init; }
        public required ClipTransform Transform { get; init; }

        /// <summary>
        /// Where this clip's pixels come from for this frame. For a SourceClip
        /// backed by video this is its optimized media (see
        /// OptimizedMediaBuilder) plus the timestamp to seek to. For an Image
        /// SourceClip or a TextClip it's a static file — the same frame every
        /// time, read once and reused rather than seeked.
        /// </summary>
        public string? SourcePath { get; init; }
        public double SourceSeekSeconds { get; init; }

        /// <summary>True when SourcePath is optimized VIDEO media needing a
        /// per-frame seek; false for a static image (including rasterized
        /// text) that reads the same way on every frame.</summary>
        public bool IsVideoSeek { get; init; }

        public int NativeWidth { get; init; }
        public int NativeHeight { get; init; }

        /// <summary>
        /// True when this clip's PreTransform effects were already baked into
        /// its optimized media (see OptimizedMedia.EffectsBaked /
        /// VideoUtils.ReencodeVideoAsync's preTransformEffects parameter),
        /// meaning ClipVideoChain.Build must NOT apply them again here —
        /// doing so would double them up. Always false for anything that
        /// isn't a video SourceClip with optimized media, since only that
        /// path bakes effects.
        /// </summary>
        public bool PreTransformEffectsBaked { get; init; }

        /// <summary>
        /// Clip-relative seconds at this output frame. Generator and noise
        /// clips are functions of their own elapsed time, so they need this
        /// even though they never decode anything.
        /// </summary>
        public double ClipSeconds { get; init; }
    }

    /// <summary>
    /// One frame's complete composite state: every clip visible on this frame,
    /// bottom channel first, each already resolved to its literal transform.
    /// </summary>
    internal sealed class FrameState
    {
        public required int FrameIndex { get; init; }
        public required List<FrameChannel> Channels { get; init; }
    }

    internal sealed class FrameChannel
    {
        public required BlendMode BlendMode { get; init; }

        /// <summary>
        /// Clips drawn on this channel this frame. Normally one — a channel's
        /// clips cannot overlap — but exactly two while a transition between
        /// adjacent clips is mid-flight, in which case Progress says how far
        /// through it is.
        /// </summary>
        public required List<FrameClip> Clips { get; init; }

        public TransitionType? Transition { get; init; }

        /// <summary>0 at the transition's first frame, 1 at its last.</summary>
        public double TransitionProgress { get; init; }
    }

    /// <summary>
    /// Builds the filter chain for a single output frame.
    ///
    /// The interface is the render-mode seam: Blueprint.HardwareAccelerator
    /// picks the implementation, and everything above this (FrameRenderer, the
    /// orchestration, the output accumulation) is identical either way. The
    /// software implementation below drives the stock CPU filters this
    /// pipeline has always used; a libplacebo/Vulkan implementation slots in
    /// beside it without the renderer knowing.
    /// </summary>
    internal interface IFrameFilterChainBuilder
    {
        /// <summary>
        /// Registers this frame's inputs on the graph and returns the label of
        /// the finished canvas-sized frame, ready to be written out.
        /// </summary>
        string Build(
            FrameState frame, InputGraph graph,
            int canvasWidth, int canvasHeight, int fps,
            ConcurrentBag<string> tempFiles);
    }

    /// <summary>
    /// The CPU/stock-filter implementation, reusing the existing clip chain
    /// (ClipVideoChain, ClipEffects, TransformExpressions) rather than
    /// reimplementing the compositing rules.
    ///
    /// Everything is a still: each input is one already-decoded frame, so the
    /// chain runs on a single-frame stream rather than a live one. That is
    /// what lets the whole graph be time-free — `enable` windows, `setpts`
    /// shifts and keyframe expressions all disappear, because the only frame
    /// in flight is the one being rendered.
    /// </summary>
    internal sealed class SoftwareFrameFilterChainBuilder : IFrameFilterChainBuilder
    {
        public string Build(
            FrameState frame, InputGraph graph,
            int canvasWidth, int canvasHeight, int fps,
            ConcurrentBag<string> tempFiles)
        {
            //the accumulator every channel draws onto, transparent so gaps stay
            //transparent — same rule the old whole-window compositor used, just
            //for one frame
            string accumulator = GraphUtilities.BuildTransparentBlank(
                graph, canvasWidth, canvasHeight, fps, FrameDurationSeconds(fps), "frbase");

            foreach (FrameChannel channel in frame.Channels)
            {
                string? drawn = ComposeChannel(
                    channel, graph, canvasWidth, canvasHeight, fps, tempFiles);

                if (drawn == null) continue;

                accumulator = Draw(
                    accumulator, drawn, channel.BlendMode, graph, canvasWidth, canvasHeight);
            }

            return Flatten(accumulator, graph, canvasWidth, canvasHeight, fps);
        }

        /// <summary>
        /// One channel's clips for this frame, transitioned together if a
        /// transition is mid-flight.
        /// </summary>
        private static string? ComposeChannel(
            FrameChannel channel, InputGraph graph,
            int canvasWidth, int canvasHeight, int fps,
            ConcurrentBag<string> tempFiles)
        {
            var built = new List<string>();

            foreach (FrameClip clip in channel.Clips)
            {
                string? label = BuildClip(clip, graph, canvasWidth, canvasHeight, fps, tempFiles);
                if (label != null) built.Add(label);
            }

            if (built.Count == 0) return null;
            if (built.Count == 1) return built[0];

            return ApplyTransition(
                built[0], built[1], channel.Transition, channel.TransitionProgress, graph, fps);
        }

        /// <summary>
        /// A transition at an arbitrary point, rendered from two stills.
        ///
        /// `xfade` computes its own progress as (pts - offset) / duration, so
        /// pinning duration to 1 and offset to -progress makes the single frame
        /// at pts=0 evaluate to exactly the progress wanted. That means every
        /// one of ffmpeg's transition types works here unchanged, at any point
        /// in its arc, with no per-type reimplementation — verified directly
        /// against both a continuous type (fade) and a hard-edged geometric one
        /// (wipeleft, whose boundary tracked the requested progress).
        ///
        /// settb=AVTB on both sides because xfade requires a matching timebase,
        /// and source files can carry unusual native ones.
        /// </summary>
        private static string ApplyTransition(
            string outgoing, string incoming,
            TransitionType? transition, double progress,
            InputGraph graph, int fps)
        {
            string name = transition != null &&
                          Constants.XfadeNames.TryGetValue(transition.Value, out string? mapped)
                ? mapped
                : "fade";

            string a = graph.NextLabel("frxfa");
            string b = graph.NextLabel("frxfb");
            graph.FilterLines.Add($"[{outgoing}]settb=AVTB[{a}]");
            graph.FilterLines.Add($"[{incoming}]settb=AVTB[{b}]");

            string joined = graph.NextLabel("frxf");
            graph.FilterLines.Add(
                $"[{a}][{b}]xfade=transition={name}:duration=1:" +
                $"offset={GraphUtilities.Num(-Math.Clamp(progress, 0.0, 1.0))}[{joined}]");

            return joined;
        }

        /// <summary>
        /// One clip's content for this frame, through the existing clip chain
        /// with its transform already resolved to a literal.
        ///
        /// The work rect is deliberately the full canvas here rather than a
        /// bounding box. The bbox optimization exists to shrink the buffer a
        /// clip is rendered in across its WHOLE duration; on a single frame
        /// there is no duration to union over, and the per-frame renderer's
        /// win comes from not holding every decoder open at once instead.
        /// Revisit if per-frame buffer size turns out to matter — it would need
        /// its own per-frame bounding-box computation over just this frame's
        /// transform.
        /// </summary>
        private static string? BuildClip(
            FrameClip frameClip, InputGraph graph,
            int canvasWidth, int canvasHeight, int fps,
            ConcurrentBag<string> tempFiles)
        {
            string? content = BuildContent(
                frameClip, graph, canvasWidth, canvasHeight, fps, tempFiles,
                out int nativeWidth, out int nativeHeight, out bool modulateApplied);

            if (content == null) return null;

            return ClipVideoChain.Build(
                frameClip.Clip, content,
                nativeWidth, nativeHeight,
                canvasWidth, canvasHeight,
                fps, FrameDurationSeconds(fps),
                graph, tempFiles,
                frameClip.Transform,
                modulateApplied,
                frameClip.PreTransformEffectsBaked);
        }

        /// <summary>
        /// This frame's raw content for one clip, before framing or transform.
        ///
        /// A video SourceClip reads a single frame out of its optimized media
        /// at the resolved timestamp — an intra-only lossless file, so the seek
        /// is exact and costs no GOP walk. Images and rasterized text are read
        /// straight off disk. Generators and noise are still generated inline,
        /// exactly as ClipContentBuilder does, just for a single frame at this
        /// clip-relative time.
        /// </summary>
        private static string? BuildContent(
            FrameClip frameClip, InputGraph graph,
            int canvasWidth, int canvasHeight, int fps,
            ConcurrentBag<string> tempFiles,
            out int nativeWidth, out int nativeHeight, out bool modulateApplied)
        {
            nativeWidth = frameClip.NativeWidth > 0 ? frameClip.NativeWidth : canvasWidth;
            nativeHeight = frameClip.NativeHeight > 0 ? frameClip.NativeHeight : canvasHeight;
            modulateApplied = false;

            switch (frameClip.Clip)
            {
                case SourceClip when frameClip.SourcePath != null && frameClip.IsVideoSeek:
                {
                    //-ss BEFORE -i so ffmpeg seeks rather than decoding from the
                    //front; exact here because the optimized media is intra-only
                    //(see VideoUtils.ReencodeVideoAsync). FrameStateResolver has
                    //already clamped SourceSeekSeconds to freeze on the last
                    //real frame if this clip outlasts its source
                    int index = graph.AddInput(
                        frameClip.SourcePath, true,
                        ["-ss", GraphUtilities.Num(frameClip.SourceSeekSeconds)]);

                    string label = graph.NextLabel("frsrc");
                    graph.FilterLines.Add(
                        $"[{index}:v]trim=end_frame=1,setpts=PTS-STARTPTS," +
                        $"format={PixelFormats.Primary},settb=AVTB[{label}]");

                    return label;
                }

                case SourceClip or TextClip when frameClip.SourcePath != null:
                {
                    //an Image SourceClip or a TextClip's already-rasterized PNG
                    //(built ONCE, up front — see FrameRenderer's static-content
                    //prep — rather than re-rasterizing identical text on every
                    //frame it happens to be visible on). Both read the same way:
                    //a static file, looped, no seek
                    int index = graph.AddInput(frameClip.SourcePath, true, ["-loop", "1"]);

                    string label = graph.NextLabel("frstatic");
                    graph.FilterLines.Add(
                        $"[{index}:v]trim=end_frame=1,setpts=PTS-STARTPTS," +
                        $"format={PixelFormats.Primary},settb=AVTB[{label}]");

                    return label;
                }

                case SourceClip:
                    //audio-only source: occupies its slot but draws nothing
                    return null;

                case GeneratorClip generator:
                {
                    nativeWidth = canvasWidth;
                    nativeHeight = canvasHeight;

                    //a generator's colour IS its state at this instant, resolved
                    //in C# rather than built as an xfade ramp — the whole reason
                    //this path exists
                    SKColorLike colour = GeneratorColourAt(generator, frameClip.ClipSeconds);

                    string label = graph.NextLabel("frgen");
                    graph.FilterLines.Add(
                        $"color={colour.Hex}:size={canvasWidth}x{canvasHeight}:rate={fps}:" +
                        $"duration={GraphUtilities.Num(FrameDurationSeconds(fps))}," +
                        $"trim=end_frame=1,setpts=PTS-STARTPTS," +
                        $"format={PixelFormats.Primary},settb=AVTB[{label}]");

                    return label;
                }

                case NoiseClip noise:
                {
                    nativeWidth = canvasWidth;
                    nativeHeight = canvasHeight;

                    double xscale = Math.Max(noise.Detail, 0f) * 1000.0;
                    double yscale = xscale * canvasHeight / (double)canvasWidth;
                    double tscale = Math.Max(noise.SeetheRate, 0f) * 10.0;
                    uint seed = unchecked((uint)noise.Seed);

                    //`perlin` derives its time coordinate from pts, and this
                    //stream only ever has frame 0 — so the clip's elapsed time
                    //has to be dialled in by starting the stream at that offset
                    //rather than by letting it run. setpts shifts frame 0's pts
                    //to the clip-relative time, so the noise seethes correctly
                    //across the render instead of freezing on its first pattern.
                    string label = graph.NextLabel("frnoise");
                    graph.FilterLines.Add(
                        $"perlin=size={canvasWidth}x{canvasHeight}:rate={fps}:" +
                        $"random_mode=seed:random_seed={seed}:" +
                        $"xscale={GraphUtilities.Num(xscale)}:" +
                        $"yscale={GraphUtilities.Num(yscale)}:" +
                        $"tscale={GraphUtilities.Num(tscale)}," +
                        $"trim=start_frame={(int)Math.Round(frameClip.ClipSeconds * fps)}:" +
                        $"end_frame={(int)Math.Round(frameClip.ClipSeconds * fps) + 1}," +
                        $"setpts=PTS-STARTPTS," +
                        $"format={PixelFormats.Primary},settb=AVTB[{label}]");

                    return label;
                }

                default:
                    throw new NotSupportedException(
                        $"Unknown Clip subtype: {frameClip.Clip.GetType().Name}");
            }
        }

        /// <summary>
        /// A GeneratorClip's colour at a given clip-relative time, resolved in
        /// C# instead of as an xfade ramp between colour sources.
        ///
        /// ClipContentBuilder builds the ColorIn/ColorMain/ColorOut ramps with
        /// xfade because a whole-clip stream needs the colour to animate over
        /// time and colorchannelmixer takes constants only. Per frame that
        /// inverts: the time is known, so the ramp is just a lerp.
        /// </summary>
        private static SKColorLike GeneratorColourAt(GeneratorClip clip, double seconds)
        {
            double total = clip.Duration.TotalSeconds;

            double fadeIn = clip.ColorIn is { } inRamp
                ? Math.Clamp(inRamp.Item2.TotalSeconds, 0, total)
                : 0;

            if (fadeIn > 0 && seconds < fadeIn)
            {
                return SKColorLike.Lerp(
                    clip.ColorIn!.Value.Item1, clip.ColorMain, seconds / fadeIn);
            }

            double fadeOut = clip.ColorOut is { } outRamp
                ? Math.Clamp(outRamp.Item2.TotalSeconds, 0, total - fadeIn)
                : 0;

            if (fadeOut > 0 && seconds > total - fadeOut)
            {
                double u = (seconds - (total - fadeOut)) / fadeOut;
                return SKColorLike.Lerp(clip.ColorMain, clip.ColorOut!.Value.Item1, u);
            }

            return SKColorLike.From(clip.ColorMain);
        }

        /// <summary>
        /// Draws a finished channel onto the accumulator.
        ///
        /// No `enable` window and no setpts shift needed here — a clip is only
        /// present in a FrameState at all if it is visible on this frame, so the
        /// gating already happened in C#.
        /// </summary>
        private static string Draw(
            string accumulator, string channel, BlendMode blendMode,
            InputGraph graph, int canvasWidth, int canvasHeight)
        {
            if (blendMode == BlendMode.Normal)
            {
                string plain = graph.NextLabel("frdraw");
                graph.FilterLines.Add(
                    $"[{accumulator}][{channel}]overlay=0:0:format=auto[{plain}]");

                return plain;
            }

            if (!Constants.BlendModeNames.TryGetValue(blendMode, out string? mode))
                throw new NotSupportedException(
                    $"Channel blend mode {blendMode} has no ffmpeg equivalent.");

            //`blend` ignores alpha and would recolour the whole frame, so the
            //blended result is masked back down by the channel's own alpha
            //before being overlaid
            string blendSource = graph.NextLabel("frblendsrc");
            string maskSource = graph.NextLabel("frblendmask");
            graph.FilterLines.Add($"[{channel}]split=2[{blendSource}][{maskSource}]");

            string mask = graph.NextLabel("frblendalpha");
            graph.FilterLines.Add(
                $"[{maskSource}]format={PixelFormats.Primary},alphaextract[{mask}]");

            string backdropCopy = graph.NextLabel("frblendbase");
            string backdropKeep = graph.NextLabel("frblendkeep");
            graph.FilterLines.Add($"[{accumulator}]split=2[{backdropCopy}][{backdropKeep}]");

            string blended = graph.NextLabel("frblended");
            graph.FilterLines.Add(
                $"[{backdropCopy}][{blendSource}]blend=all_mode={mode}:shortest=0[{blended}]");

            string masked = graph.NextLabel("frblendmasked");
            graph.FilterLines.Add($"[{blended}][{mask}]alphamerge[{masked}]");

            string next = graph.NextLabel("frdraw");
            graph.FilterLines.Add(
                $"[{backdropKeep}][{masked}]overlay=0:0:format=auto[{next}]");

            return next;
        }

        /// <summary>
        /// Drops the composite onto opaque black.
        ///
        /// This deliberately does NOT convert to yuv420p:
        /// the frame is written out as lossless rgba64le and only converted at
        /// the very end, when the accumulated output is encoded. Converting
        /// here would throw away precision on every single frame and make the
        /// intermediate lossy for no reason.
        /// </summary>
        private static string Flatten(
            string accumulator, InputGraph graph, int canvasWidth, int canvasHeight, int fps)
        {
            string backdrop = graph.NextLabel("frflatbg");
            graph.FilterLines.Add(
                $"color=black:size={canvasWidth}x{canvasHeight}:rate={fps}:" +
                $"duration={GraphUtilities.Num(FrameDurationSeconds(fps))}," +
                $"format={PixelFormats.Primary},settb=AVTB[{backdrop}]");

            string flattened = graph.NextLabel("frout");
            graph.FilterLines.Add(
                $"[{backdrop}][{accumulator}]overlay=0:0:format=auto:shortest=1[{flattened}]");

            return flattened;
        }

        /// <summary>
        /// One frame's worth of time. Sources in this graph are stills, but a
        /// `color`/`perlin` source still needs a duration, and it has to be at
        /// least one frame at the output rate or the source produces nothing.
        /// </summary>
        private static double FrameDurationSeconds(int fps) => 1.0 / fps;
    }

    /// <summary>
    /// A colour resolved to ffmpeg's syntax, kept separate from SkiaSharp's
    /// SKColor so the lerp and the hex formatting live in one place.
    ///
    /// The alpha suffix is not optional: `color=0xRRGGBB` renders fully opaque
    /// no matter what alpha was intended — the same trap ClipContentBuilder.Hex
    /// documents.
    /// </summary>
    internal readonly record struct SKColorLike(byte R, byte G, byte B, byte A)
    {
        public static SKColorLike From(SkiaSharp.SKColor c) => new(c.Red, c.Green, c.Blue, c.Alpha);

        public static SKColorLike Lerp(SkiaSharp.SKColor from, SkiaSharp.SKColor to, double u)
        {
            u = Math.Clamp(u, 0.0, 1.0);

            return new SKColorLike(
                (byte)Math.Round(from.Red + ((to.Red - from.Red) * u)),
                (byte)Math.Round(from.Green + ((to.Green - from.Green) * u)),
                (byte)Math.Round(from.Blue + ((to.Blue - from.Blue) * u)),
                (byte)Math.Round(from.Alpha + ((to.Alpha - from.Alpha) * u)));
        }

        public string Hex =>
            $"0x{R:x2}{G:x2}{B:x2}@{GraphUtilities.Num(A / 255.0)}";
    }
}
