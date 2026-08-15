using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components.Effects;
using EditSharp.Components;
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
        ///
        /// REINSTATED after being removed as a suspected bottleneck: a real A/B
        /// test on the actual render (6-clip test blueprint, drop shadow removed)
        /// showed removing it saved only ~18ms per clip (4426ms -> 4317ms for 6
        /// clips) — nowhere near enough to explain the multi-second per-frame
        /// cost. The real cost turned out to be that EVERY operation in a clip's
        /// chain runs at full canvas resolution regardless of the clip's actual
        /// on-screen size (see ComputeWorkRect below) — supersampling one of a
        /// dozen-plus full-canvas passes was never going to move the needle much
        /// on its own.
        /// </summary>
        public const int MaskSupersample = 2;


        /// <summary>
        /// Builds the whole chain. contentLabel must be a trimmed video stream at
        /// the source's native resolution; the returned label is canvas-sized RGBA.
        ///
        /// literalTransform is the clip's state already resolved to this exact
        /// render's point in time (see Clip.TransformAt) — there is no other
        /// caller left that renders a clip's whole keyframe animation as one
        /// continuous stream, so there is nothing left for this to be optional
        /// against.
        ///
        /// Frame/PreTransform effects/Modulate/the perspective warp all run
        /// against TransformExpressions.ComputeWorkRect — the clip's actual
        /// footprint on canvas this frame, not the full canvas regardless of
        /// how small the clip appears. Every one of those stages runs at full
        /// canvas resolution for every clip on every frame otherwise, which
        /// measured out as the dominant per-frame render cost on a real render
        /// (roughly 500ms/clip in filter execution, ~90% of total frame time
        /// on a 6-clip test). The result is padded back out to canvas size
        /// BEFORE PostTransform effects run, not after — DropShadowEffect's own
        /// remarks are explicit that it needs canvas-sized room to blur and
        /// offset into, and this keeps that guarantee exactly as it was, at
        /// the cost of not shrinking PostTransform effects' own cost the same
        /// way. Explicitly scoped this way, matching what was asked: this pass
        /// covers Frame/PreTransform/Modulate/Transform, not PostTransform.
        /// </summary>
        public static string Build(
            Clip clip,
            string contentLabel,
            int nativeWidth, int nativeHeight,
            int canvasWidth, int canvasHeight,
            int fps, double durationSeconds,
            InputGraph graph, ConcurrentBag<string> tempFiles,
            ClipTransform literalTransform,
            bool modulateAlreadyApplied = false,
            bool preTransformEffectsBaked = false)
        {
            TransformExpressions.WorkRect rect = TransformExpressions.ComputeWorkRect(
                clip, literalTransform, nativeWidth, nativeHeight, canvasWidth, canvasHeight);

            //IDENTITY FAST PATH eligibility. Two conditions, both required:
            //
            //  1. THIS frame's resolved transform is the exact no-op.
            //  2. The clip never scales beyond 1x ANYWHERE in its keyframe
            //     range (or its un-keyed Transform, if it has no keyframes).
            //
            //Condition 2 exists because ComputeContentSize below doesn't size
            //content for this frame — it sizes it for the clip's LARGEST
            //keyframed scale, so a later zoom stays sharp (see its own
            //remarks). A clip that's animated elsewhere but happens to rest
            //on an identity frame right now can still be holding content
            //sized for a bigger moment; skipping the warp there wouldn't
            //just leave a cosmetic artifact, it would render that oversized
            //content at full size instead of shrunk to this frame's actual
            //on-screen size. Only a clip whose scale never exceeds 1x
            //anywhere is guaranteed to already have content sized exactly
            //right, independent of which frame this is.
            (double maxScaleX, double maxScaleY) = TransformExpressions.MaxScale(clip);
            bool identity = IsIdentityTransform(literalTransform) &&
                             maxScaleX <= 1.0 && maxScaleY <= 1.0;

            int contentWidth, contentHeight;

            if (identity)
            {
                //No perspective warp is going to run for this clip, so there
                //is no reason to reserve TransformExpressions.
                //TransparentBorderPixels of margin for one to sample into.
                //That margin exists ONLY to give the warp something to clamp
                //against at its edge, and is normally invisible because the
                //warp's own quad math (OutsetToFrame) deliberately projects
                //destination corners LARGER than the frame, stretching the
                //border-reserved content back out and cropping the margin
                //away. Skipping the warp while still sizing content through
                //the border-reserving ComputeContentSize would leave that
                //margin as a real, visible transparent gap around the clip
                //instead of erasing it. Sizing off the plain aspect fit
                //instead avoids ever creating the margin in the first place.
                var (baseW, baseH) = TransformExpressions.BaseFitSize(
                    nativeWidth, nativeHeight, canvasWidth, canvasHeight);
                contentWidth = Math.Max(2, (int)Math.Round(baseW));
                contentHeight = Math.Max(2, (int)Math.Round(baseH));
            }
            else
            {
                (contentWidth, contentHeight) = TransformExpressions.ComputeContentSize(
                    clip, nativeWidth, nativeHeight, canvasWidth, canvasHeight);
            }

            //the content is CENTRED in the frame, which is what makes the frame's
            //model-space outset symmetric — see PlaceInFrame
            TransformExpressions.ContentPlacement placement =
                TransformExpressions.PlaceInFrame(
                    contentWidth, contentHeight, rect.Width, rect.Height);

            var context = new ClipChainContext(
                canvasWidth, canvasHeight, rect, fps, durationSeconds,
                placement, graph, tempFiles);

            string framed = Frame(contentLabel, context);

            //baked means OptimizedMediaBuilder already ran these same PreTransform
            //effects once, over the whole clip, when building its optimized media
            //(see VideoUtils.BuildPreTransformChain) — applying them again here
            //would double them up (a blur blurred twice, rounded corners rounded
            //again against already-rounded alpha)
            string preEffects = preTransformEffectsBaked
                ? framed
                : ClipEffects.ApplyStage(clip.Effects, EffectStage.PreTransform, framed, context);

            //a generator clip's colour IS its Modulate, baked in when the source
            //was generated — applying it again here would square it
            string modulated = modulateAlreadyApplied
                ? preEffects
                : ApplyModulate(clip, preEffects, context);

            //See the `identity` computation above: true only when this frame's
            //transform is an exact no-op AND the clip never scales beyond 1x
            //anywhere in its keyframe range, which together guarantee content
            //was sized (border-free, above) to already match exactly what
            //this frame needs — nothing left for the warp to do. Transformed
            //clips are completely unaffected by any of this — same warp, same
            //MaskSupersample, same antialiasing, exactly as before.
            string transformed = identity
                ? modulated
                : ApplyTransform(modulated, nativeWidth, nativeHeight, context, literalTransform);

            //everything above ran at the tight work rect's size. Pad back out to
            //full canvas — a pure border fill, no resampling — and build a
            //canvas-sized context for PostTransform effects, which still expect
            //exactly the canvas-sized frame they always got (see the class
            //remarks above on why this pass doesn't shrink PostTransform's own
            //cost the same way)
            if (rect.IsFullCanvas(canvasWidth, canvasHeight))
                return ClipEffects.ApplyStage(clip.Effects, EffectStage.PostTransform, transformed, context);

            string paddedToCanvas = graph.NextLabel("clpad");
            graph.FilterLines.Add(
                $"[{transformed}]pad={canvasWidth}:{canvasHeight}:{rect.X}:{rect.Y}:color=black@0[{paddedToCanvas}]");

            var canvasRect = TransformExpressions.WorkRect.FullCanvas(canvasWidth, canvasHeight);
            var canvasContext = new ClipChainContext(
                canvasWidth, canvasHeight, canvasRect, fps, durationSeconds,
                new TransformExpressions.ContentPlacement(canvasWidth, canvasHeight, 0, 0),
                graph, tempFiles);

            return ClipEffects.ApplyStage(
                clip.Effects, EffectStage.PostTransform, paddedToCanvas, canvasContext);
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
                $"[{contentLabel}]scale={p.Width}:{p.Height},setsar=1,fps={context.Fps}," +
                $"format={PixelFormats.Primary}," +
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
        /// True when a transform contributes nothing: dead centre, unscaled,
        /// unrotated. Exact equality is deliberate, not an approximation —
        /// these values come from either an explicit default or a
        /// Clip.TransformAt lerp between two keyframes, and lerping between
        /// two values that are themselves exactly identity produces the
        /// exact identity value back out with no floating-point drift (the
        /// delta term is exactly zero, not merely close to it, so `from +
        /// (to - from) * t` collapses to `from` exactly for any t).
        /// </summary>
        private static bool IsIdentityTransform(ClipTransform t) =>
            t.Position.X == 0f && t.Position.Y == 0f &&
            t.Scale.X == 1f && t.Scale.Y == 1f &&
            t.Rotation == 0f && t.Pitch == 0f && t.Yaw == 0f;

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
            string label, int nativeWidth, int nativeHeight, ClipChainContext context,
            ClipTransform literalTransform)
        {
            //the clip's state is already resolved to this exact frame's time
            //(see Clip.TransformAt / FrameStateResolver), so the corners are
            //eight literal numbers with no keyframe expression at all
            string perspective = TransformExpressions.BuildLiteralPerspectiveArgs(
                literalTransform, nativeWidth, nativeHeight,
                context.CanvasWidth, context.CanvasHeight,
                context.FrameWidth, context.FrameHeight,
                context.OffsetX, context.OffsetY, context.Placement);

            //the mask is warped at MaskSupersample scale, so it needs the same quad
            //expressed in those larger coordinates
            string perspectiveLarge = MaskSupersample == 1
                ? perspective
                : TransformExpressions.BuildLiteralPerspectiveArgs(
                    literalTransform, nativeWidth, nativeHeight,
                    context.CanvasWidth, context.CanvasHeight,
                    context.FrameWidth, context.FrameHeight,
                    context.OffsetX, context.OffsetY, context.Placement, MaskSupersample);

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
                $"[{alphaCopy}]format={PixelFormats.Primary},alphaextract," +
                $"{upscale}{perspectiveLarge}{downscale}[{warpedAlpha}]");

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
                $"[{colourCopy}]format={PixelFormats.Gbrp}," +
                $"fillborders=left={left}:right={right}:top={top}:bottom={bottom}:mode=smear," +
                $"{perspective},format={PixelFormats.Gbrap}[{warpedColour}]");

            string merged = context.Graph.NextLabel("cltf");
            context.Graph.FilterLines.Add($"[{warpedColour}][{warpedAlpha}]alphamerge[{merged}]");

            return merged;
        }
    }

    /// <summary>
    /// The pixel formats the whole pipeline works in.
    ///
    /// EVERYTHING is 16-bit. This is not a per-path or per-render-mode choice:
    /// the pipeline moved off 8-bit wholesale, so there is exactly one format
    /// set and no switch to get wrong. Splitting a render into many independent
    /// per-frame ffmpeg processes means more discrete quantization touchpoints
    /// than the old single-filter_complex design had — at 16 bits that rounding
    /// is far below anything visible, and the final encode is the only place
    /// precision is deliberately given up.
    ///
    /// Primary is gbrap16le, NOT rgba64le. It used to be rgba64le, on the
    /// assumption that requesting "-pix_fmt rgba64le" on the FFV1 encoder used
    /// for optimized media would produce genuinely packed RGBA. It doesn't:
    /// FFV1 has no packed-RGBA mode at all, so ffmpeg silently substitutes its
    /// own native planar equivalent — confirmed directly from a real render's
    /// stream headers, which reported the encoded file as gbrap16le regardless
    /// of what was requested. Every per-frame read of that "rgba64le" file was
    /// therefore paying a real, unaccelerated gbrap16le→rgba64le conversion
    /// (confirmed: ffmpeg logs "No accelerated colorspace conversion found")
    /// on every single frame, for a conversion that existed purely because the
    /// constant's assumed format didn't match what was actually on disk.
    /// Renaming to Primary and setting it to gbrap16le — the format FFV1
    /// actually stores — makes the encode step honest (no silent substitution)
    /// and removes that conversion everywhere downstream reads it. Every
    /// filter this pipeline uses on the "primary" format (perspective,
    /// alphaextract, alphamerge, colorchannelmixer, fillborders, blend, pad,
    /// overlay, xfade) was already confirmed to accept gbrap16le just as
    /// readily as rgba64le, since Gbrap below is the identical format already
    /// used for the colour-blur path — this is a value change, not a filter
    /// compatibility question.
    ///
    /// Gbrp/Gbrap/Gray are unchanged: gbrp16le, gbrap16le, gray16le. All were
    /// confirmed by direct test to be accepted by every filter in the chain,
    /// and `lut` in particular was checked to scale proportionally rather than
    /// assuming an 8-bit range — the drop shadow's opacity and its zero-fill
    /// both depend on that.
    /// </summary>
    internal static class PixelFormats
    {
        public const string Primary = "gbrap16le";
        public const string Gbrp = "gbrp16le";
        public const string Gbrap = "gbrap16le";
        public const string Gray = "gray16le";

        //gbrap16le: 4 planes (G, B, R, A), each a full-resolution 16-bit
        //(2-byte) sample with no chroma subsampling — unlike a YUV format,
        //every plane is the same width x height. Total bytes per pixel
        //across all four planes: 4 x 2 = 8. Kept next to Primary itself
        //rather than as a separate hardcoded constant elsewhere, since
        //anything that needs to know a raw Primary-format buffer's exact
        //byte size (see FrameRenderer's per-frame pipe read) has to stay in
        //lockstep with this specific format — the same single-source-of-
        //truth reasoning as PixelFormatFor referencing Primary directly
        //instead of its own separate "gbrap16le" literal.
        public const int PrimaryBytesPerPixel = 8;
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
        InputGraph graph, ConcurrentBag<string> tempFiles,
        bool repeatStaticInputs = false)
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

        //true only when this context is building a WHOLE-CLIP filter chain for
        //optimized-media baking (see VideoUtils.BuildPreTransformChain) rather
        //than a single per-frame still. A static single-frame input like the
        //rounded-corners mask needs -loop 1 to persist across every frame of a
        //continuous re-encode; the ordinary per-frame path renders exactly one
        //frame per ffmpeg process, so the same mask file is naturally read once
        //and never needs to loop
        public bool RepeatStaticInputs { get; } = repeatStaticInputs;
    }
}
