using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components.Effects;
using EditSharp.Components.Clips;

namespace EditSharp.Render
{
    /// <summary>
    /// Takes one clip's already-trimmed content stream and produces a canvas-sized
    /// RGBA stream ready to composite onto its channel.
    ///
    /// The order is fixed:
    ///   frame -> pre-transform effects -> Modulate -> transform -> post-transform
    ///   effects
    ///
    /// FRAMING
    /// The content is scaled to the canvas MINUS a transparent border and padded
    /// out to full canvas. The border is not cosmetic: `perspective` clamps to
    /// edge pixels when it samples outside the source, so a borderless frame warps
    /// to a fully opaque rectangle and the quad never appears at all. The border
    /// gives it genuinely transparent pixels to reach, and interpolating across
    /// that alpha step is also what antialiases the warped edge.
    ///
    /// The scale to canvas size deliberately ignores aspect ratio. The warp undoes
    /// it, because the target quad is built from the content's true aspect — see
    /// TransformExpressions for why that beats padding plus a homography.
    ///
    /// COLOUR/MASK SPLIT
    /// Everything that resamples runs on colour and alpha as separate streams,
    /// recombined with alphamerge. Warping straight (non-premultiplied) RGBA makes
    /// interpolation blend real colour against the transparent border's zeroes,
    /// darkening pixels that are still mostly transparent — measured at 127/255 on
    /// a red test clip, versus 255/255 for the split, with identical edge
    /// antialiasing either way. The colour side gets its border smeared outward
    /// first so the warp always has real content to sample.
    /// </summary>
    internal static class ClipVideoChain
    {

        /// <summary>
        /// How many times larger than final the ALPHA MASK is warped before being
        /// resolved back down.
        ///
        /// `perspective` decides inside/outside per output pixel with no coverage
        /// antialiasing, so any edge that isn't axis-aligned stair-steps. The
        /// transparent border softens this a little, but the values it produces are
        /// bilinear samples of a hard step rather than real area coverage — along a
        /// rotated edge consecutive rows come out with coverage jumping around
        /// non-monotonically, which is exactly what reads as jaggies.
        ///
        /// Warping the mask larger and resolving down with a box average
        /// (flags=area) produces genuine coverage. Measured against a 16x reference,
        /// mean edge error out of 255, with cost relative to no supersampling:
        ///
        ///     1x  error 42.3   cost 1.0x     (stair-steps visibly)
        ///     2x  error 17.8   cost 1.6x     (default)
        ///     3x  error 10.7   cost 2.6x
        ///     4x  error  7.3   cost 4.2x
        ///
        /// 2 is the value point — it removes well over half the error for a little
        /// over half again the time. Raise it if edges still read as hard on large
        /// rotated content; the cost is quadratic, and at 4K it is charged against
        /// an already sizeable frame.
        ///
        /// Only the mask is supersampled. The colour plane is masked by it, so its
        /// own edges never show.
        /// </summary>
        public const int MaskSupersample = 2;


        /// <summary>
        /// Builds the whole chain. contentLabel must be a trimmed video stream at
        /// the source's native resolution; the returned label is canvas-sized RGBA.
        /// </summary>
        public static string Build(
            Clip clip,
            string contentLabel,
            int nativeWidth, int nativeHeight,
            int canvasWidth, int canvasHeight,
            int fps, double durationSeconds,
            InputGraph graph, ConcurrentBag<string> tempFiles,
            bool modulateAlreadyApplied = false,
            TransformExpressions.WorkRect? workRect = null)
        {
            //no work rect supplied means the old behaviour: render this clip in a
            //full-canvas frame at the origin. Every fallback path relies on that
            //being byte-for-byte what the pipeline did before bounding boxes
            //existed, so it is expressed as the general case with frame == canvas
            //rather than as a separate branch.
            TransformExpressions.WorkRect rect =
                workRect ?? TransformExpressions.WorkRect.FullCanvas(canvasWidth, canvasHeight);

            var (contentWidth, contentHeight) = TransformExpressions.ComputeContentSize(
                clip, nativeWidth, nativeHeight, canvasWidth, canvasHeight);

            //the content is CENTRED in the work frame, which is what makes the
            //frame's model-space outset symmetric — see PlaceInFrame
            TransformExpressions.ContentPlacement placement =
                TransformExpressions.PlaceInFrame(
                    contentWidth, contentHeight, rect.Width, rect.Height);

            var context = new ClipChainContext(
                canvasWidth, canvasHeight, rect, fps, durationSeconds,
                placement, graph, tempFiles);

            string framed = Frame(contentLabel, context);

            string preEffects = ClipEffects.ApplyStage(
                clip.Effects, EffectStage.PreTransform, framed, context);

            //a generator clip's colour IS its Modulate, baked in when the source
            //was generated — applying it again here would square it
            string modulated = modulateAlreadyApplied
                ? preEffects
                : ApplyModulate(clip, preEffects, context);

            string transformed = ApplyTransform(
                clip, modulated, nativeWidth, nativeHeight, context);

            return ClipEffects.ApplyStage(
                clip.Effects, EffectStage.PostTransform, transformed, context);
        }

        /// <summary>
        /// Scales the content to the canvas minus the transparent border, then pads
        /// it back out to full canvas. See the class remarks for why the border has
        /// to be there.
        /// </summary>
        private static string Frame(string contentLabel, ClipChainContext context)
        {
            var p = context.Placement;

            //scaled to roughly its on-screen size, NOT stretched to fill the frame:
            //this is where the minification happens, in a filter with a proper
            //resampling kernel, instead of in `perspective`'s two-tap bilinear
            string framed = context.Graph.NextLabel("clframe");
            context.Graph.FilterLines.Add(
                $"[{contentLabel}]scale={p.Width}:{p.Height},setsar=1,fps={context.Fps},format=rgba," +
                $"pad={context.FrameWidth}:{context.FrameHeight}:{p.X}:{p.Y}:color=black@0[{framed}]");

            return framed;
        }

        /// <summary>
        /// Applies Clip.Modulate as a per-channel multiply, at the end of the
        /// pre-transform stage. Its alpha rides along in the same operation, so
        /// clip-wide opacity is already baked into the alpha channel by the time
        /// the colour/mask split happens — nothing downstream has to special-case
        /// it. White with full alpha is the identity and is skipped entirely.
        /// </summary>
        private static string ApplyModulate(Clip clip, string label, ClipChainContext context)
        {
            var colour = clip.Modulate;
            if (colour.Red == 255 && colour.Green == 255 && colour.Blue == 255 && colour.Alpha == 255)
                return label;

            string next = context.Graph.NextLabel("clmod");
            context.Graph.FilterLines.Add(
                $"[{label}]colorchannelmixer=" +
                $"rr={GraphUtilities.Num(colour.Red / 255.0)}:" +
                $"gg={GraphUtilities.Num(colour.Green / 255.0)}:" +
                $"bb={GraphUtilities.Num(colour.Blue / 255.0)}:" +
                $"aa={GraphUtilities.Num(colour.Alpha / 255.0)}[{next}]");

            return next;
        }

        /// <summary>
        /// The transform itself: one `perspective` per stream, colour and alpha
        /// warped separately by the identical corners and recombined.
        ///
        /// The colour stream drops its alpha and has the border smeared outward
        /// first, so that wherever the warp lands on a partially covered pixel
        /// there is real content colour underneath rather than the transparent
        /// border's zeroes. The alpha stream carries the actual shape, including
        /// any transparency the content brought with it.
        /// </summary>
        private static string ApplyTransform(
            Clip clip, string label, int nativeWidth, int nativeHeight, ClipChainContext context)
        {
            string perspective = TransformExpressions.BuildPerspectiveArgs(
                clip, nativeWidth, nativeHeight,
                context.CanvasWidth, context.CanvasHeight,
                context.FrameWidth, context.FrameHeight,
                context.OffsetX, context.OffsetY, context.Fps);

            //the mask is warped at MaskSupersample scale, so it needs the same quad
            //expressed in those larger coordinates
            string perspectiveLarge = MaskSupersample == 1
                ? perspective
                : TransformExpressions.BuildPerspectiveArgs(
                    clip, nativeWidth, nativeHeight,
                    context.CanvasWidth, context.CanvasHeight,
                    context.FrameWidth, context.FrameHeight,
                    context.OffsetX, context.OffsetY, context.Fps, MaskSupersample);

            //filter_complex labels are single-consumer, so the stream has to be
            //split before feeding both the colour and the alpha path
            string colourCopy = context.Graph.NextLabel("cltfcol");
            string alphaCopy = context.Graph.NextLabel("cltfalpha");
            context.Graph.FilterLines.Add($"[{label}]split=2[{colourCopy}][{alphaCopy}]");

            //flags=neighbor on the way up so the step stays hard and the area
            //average on the way down measures real coverage — a smooth upscale
            //would just pre-blur the edge and defeat the point
            string upscale = MaskSupersample == 1
                ? ""
                : $"scale={context.FrameWidth * MaskSupersample}:" +
                  $"{context.FrameHeight * MaskSupersample}:flags=neighbor,";

            string downscale = MaskSupersample == 1
                ? ""
                : $",scale={context.FrameWidth}:{context.FrameHeight}:flags=area";

            string warpedAlpha = context.Graph.NextLabel("cltfmask");
            context.Graph.FilterLines.Add(
                $"[{alphaCopy}]format=rgba,alphaextract,{upscale}{perspectiveLarge}{downscale}[{warpedAlpha}]");

            //The smear has to reach the CONTENT's edge, not the frame's. Now that
            //the content is sized to its on-screen size it can sit well inside the
            //frame, and a fixed narrow border would leave a wide band of black
            //between the two — which the warp then blends into the content edge,
            //bringing back the dark fringe the split was meant to remove. Measured
            //on a 15 degree rotation with the content inset: a fixed 8px smear
            //gives 127/255 at partial-alpha pixels, these borders give 255/255.
            var p = context.Placement;
            int left = p.X;
            int right = context.FrameWidth - p.X - p.Width;
            int top = p.Y;
            int bottom = context.FrameHeight - p.Y - p.Height;

            string warpedColour = context.Graph.NextLabel("cltfrgb");
            context.Graph.FilterLines.Add(
                $"[{colourCopy}]format=gbrp," +
                $"fillborders=left={left}:right={right}:top={top}:bottom={bottom}:mode=smear," +
                $"{perspective},format=gbrap[{warpedColour}]");

            string merged = context.Graph.NextLabel("cltf");
            context.Graph.FilterLines.Add($"[{warpedColour}][{warpedAlpha}]alphamerge[{merged}]");

            return merged;
        }
    }

    /// <summary>
    /// The values every step of the clip chain needs, bundled so each one doesn't
    /// carry eight parameters.
    /// </summary>
    internal sealed class ClipChainContext(
        int canvasWidth, int canvasHeight,
        TransformExpressions.WorkRect workRect,
        int fps, double durationSeconds,
        TransformExpressions.ContentPlacement placement,
        InputGraph graph, ConcurrentBag<string> tempFiles)
    {
        //TWO sizes, and the distinction is the whole point of the bounding-box
        //work. CANVAS is the finished output, and is what every NORMALIZED value
        //resolves against — a blur radius, a shadow offset, the projection's own
        //camera. FRAME is the buffer this clip is actually rendered in, and is
        //what every BUFFER SIZE uses — pads, crops, supersample targets. Mixing
        //them up does not fail loudly: it silently rescales effects or moves the
        //clip, so the rule is worth stating rather than inferring.
        public int CanvasWidth { get; } = canvasWidth;
        public int CanvasHeight { get; } = canvasHeight;

        public TransformExpressions.WorkRect WorkRect { get; } = workRect;
        public int FrameWidth => WorkRect.Width;
        public int FrameHeight => WorkRect.Height;
        public int OffsetX => WorkRect.X;
        public int OffsetY => WorkRect.Y;

        public int Fps { get; } = fps;
        public double DurationSeconds { get; } = durationSeconds;

        //where the content actually sits inside the WORK frame
        public TransformExpressions.ContentPlacement Placement { get; } = placement;
        public InputGraph Graph { get; } = graph;
        public ConcurrentBag<string> TempFiles { get; } = tempFiles;
    }
}
