using System;
using System.Linq;
using SkiaSharp;
using EditSharp.Components.Effects;
using EditSharp.Components.Clips;

namespace EditSharp.Render
{
    /// <summary>
    /// Additions to TransformExpressions for the SkiaSharp compositor pivot.
    /// Everything upstream of this (ProjectCorner, ComputeQuad, ComputeWorkRect,
    /// ComputeContentPlacement) is UNCHANGED — it never talked to ffmpeg, it just
    /// produced eight numbers. Only the "eight numbers -> filter string" tail end
    /// (BuildLiteralPerspectiveArgs) is being replaced, with "eight numbers ->
    /// SKMatrix" instead.
    ///
    /// WHAT CHANGES CONCEPTUALLY, NOT JUST MECHANICALLY:
    /// `perspective` maps the FRAME's corners (frameWidth x frameHeight, with the
    /// content centred inside it per ContentPlacement) to the Quad. That's still
    /// exactly what this does. The frame rectangle is source space; Quad is
    /// destination space. Skia's SKMatrix is a genuine 3x3 projective matrix (it
    /// has persp0/persp1/persp2 terms, not just the 2x3 affine subset), so a
    /// single matrix reproduces this mapping exactly — no need for the
    /// colour/alpha split, no MaskSupersample hack. Skia's SKCanvas.DrawImage
    /// rasterizes a warped quad with real coverage-based antialiasing and (with
    /// mipmapped SKSamplingOptions) real minification filtering, which is the
    /// thing MaskSupersample and the "size content to on-screen size before the
    /// warp" trick were both working around for `perspective`'s two-tap bilinear
    /// sampler. Both of those become unnecessary — not something to port.
    /// </summary>
    internal static class SkTransformExpressions
    {
        /// <summary>
        /// TransparentBorderPixels/ContentPlacement's border reasoning also
        /// doesn't carry over, for the same root cause as MaskSupersample:
        /// it existed because `perspective` clamps to edge pixels when
        /// sampling outside its source, so a borderless frame warped to a
        /// fully opaque rectangle. DrawImage has no such clamp — sampling
        /// outside the source image is simply not drawn, so there is nothing
        /// for a transparent margin to protect against. BuildLiteralMatrix
        /// below maps content's own tight rect directly, no border padding.
        /// </summary>


        /// <summary>
        /// The SKMatrix equivalent of BuildLiteralPerspectiveArgs, now drawing
        /// straight in CANVAS space rather than a WorkRect-relative frame —
        /// WorkRect's offset/tight-buffer machinery was there to keep ffmpeg
        /// filter nodes from processing full-canvas buffers regardless of a
        /// clip's actual footprint; SKCanvas.DrawImage's cost already tracks
        /// the warped quad's actual device-pixel footprint, so that machinery
        /// has nothing left to do here. No offsetX/offsetY, and the "frame"
        /// being mapped is the content's OWN native-fit size (BaseFitSize x
        /// MaxScale, i.e. what ComputeContentSize returns), not a WorkRect.
        ///
        /// content is assumed already rasterized at contentWidth x
        /// contentHeight (see ComputeContentSize) — this matrix maps THAT
        /// rect onto the canvas-space Quad.
        /// </summary>
        public static SKMatrix BuildLiteralMatrix(
            ClipTransform transform,
            int nativeWidth, int nativeHeight,
            int canvasWidth, int canvasHeight,
            int contentWidth, int contentHeight)
        {
            // placement is now purely "content centred at (0,0) in its own
            // local rect" — ComputeQuad still wants a ContentPlacement to
            // derive OutsetToFrame's ratio from, but frame == content here,
            // so the outset ratio is exactly 1:1 (no border term applies —
            // see note below on TransparentBorderPixels).
            var placement = new TransformExpressions.ContentPlacement(
                contentWidth, contentHeight, 0, 0);

            TransformExpressions.Quad q = TransformExpressions.ComputeQuad(
                transform, nativeWidth, nativeHeight, canvasWidth, canvasHeight,
                contentWidth, contentHeight, 0, 0, placement);

            return RectToQuad(contentWidth, contentHeight, q);
        }

        /// <summary>
        /// The general "unit-square-to-quadrilateral" homography (Heckbert),
        /// composed with a pre-scale so the SOURCE is an arbitrary WxH rect
        /// instead of the unit square. This is exactly the closed-form solve
        /// `perspective`'s sense=destination performs internally — verifying
        /// this against BuildLiteralPerspectiveArgs on the same transform is
        /// the concrete thing to check corner-for-corner before trusting it
        /// (checklist item 4).
        ///
        /// Quad corner order matches TransformExpressions.Quad: (X0,Y0)=TL,
        /// (X1,Y1)=TR, (X2,Y2)=BL, (X3,Y3)=BR. The Heckbert solve wants a
        /// CYCLIC quad (TL,TR,BR,BL), so BR/BL are swapped going in.
        /// </summary>
        public static SKMatrix RectToQuad(double width, double height, TransformExpressions.Quad q)
        {
            SKMatrix unitToQuad = UnitSquareToQuad(
                q.X0, q.Y0,   // P0 = TL
                q.X1, q.Y1,   // P1 = TR
                q.X3, q.Y3,   // P2 = BR  (cyclic order, not Quad's own index order)
                q.X2, q.Y2);  // P3 = BL

            SKMatrix rectToUnit = SKMatrix.CreateScale(
                (float)(1.0 / width), (float)(1.0 / height));

            // Apply rectToUnit first, then unitToQuad.
            return SKMatrix.Concat(unitToQuad, rectToUnit);
        }

        /// <summary>
        /// Maps (0,0)->P0, (1,0)->P1, (1,1)->P2, (0,1)->P3. Falls back to the
        /// plain affine (parallelogram) solve when the quad has no actual
        /// perspective component (dx3==dy3==0) — cheaper and exact, and avoids
        /// a division by a determinant that IS legitimately zero in that case.
        /// </summary>
        private static SKMatrix UnitSquareToQuad(
            double x0, double y0, double x1, double y1,
            double x2, double y2, double x3, double y3)
        {
            double dx1 = x1 - x2, dx2 = x3 - x2, dx3 = x0 - x1 + x2 - x3;
            double dy1 = y1 - y2, dy2 = y3 - y2, dy3 = y0 - y1 + y2 - y3;

            if (dx3 == 0.0 && dy3 == 0.0)
            {
                // Parallelogram: pure affine, (1,1) is redundant / already implied.
                return new SKMatrix
                {
                    ScaleX = (float)(x1 - x0), SkewY = (float)(y1 - y0),
                    SkewX = (float)(x3 - x0), ScaleY = (float)(y3 - y0),
                    TransX = (float)x0, TransY = (float)y0,
                    Persp0 = 0, Persp1 = 0, Persp2 = 1
                };
            }

            double denom = (dx1 * dy2) - (dx2 * dy1);
            double a13 = ((dx3 * dy2) - (dx2 * dy3)) / denom;
            double a23 = ((dx1 * dy3) - (dx3 * dy1)) / denom;

            double a11 = x1 - x0 + (a13 * x1);
            double a21 = x3 - x0 + (a23 * x3);
            double a31 = x0;

            double a12 = y1 - y0 + (a13 * y1);
            double a22 = y3 - y0 + (a23 * y3);
            double a32 = y0;

            return new SKMatrix
            {
                ScaleX = (float)a11, SkewY = (float)a12,
                SkewX = (float)a21, ScaleY = (float)a22,
                TransX = (float)a31, TransY = (float)a32,
                Persp0 = (float)a13, Persp1 = (float)a23, Persp2 = 1
            };
        }
    }

    // ---------------------------------------------------------------------
    // Item 4, remaining piece: ClipChainContext shrunk now that WorkRect is
    // gone, and the full per-clip chain wired end to end (PreTransform ->
    // Modulate -> warp-draw -> PostTransform). Replaces ClipVideoChain as a
    // whole, not just ApplyTransform.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Everything a clip's chain needs, post-WorkRect. Two sizes only, and
    /// they're now genuinely just "the two things every step needs a size
    /// for" rather than a source/destination-buffer distinction:
    ///   - ContentWidth/Height: what PreTransform effects and Modulate
    ///     operate on (the ComputeContentSize raster).
    ///   - CanvasWidth/Height: what the final matrix maps ONTO, and what
    ///     PostTransform effects (DropShadow etc., item 5) operate on.
    /// No offset, no frame-vs-canvas split — a clip's own local raster has
    /// no position of its own until BuildLiteralMatrix places it.
    /// </summary>
    internal readonly struct SkClipChainContext(
        int canvasWidth, int canvasHeight,
        int contentWidth, int contentHeight,
        int fps, double durationSeconds)
    {
        public int CanvasWidth { get; } = canvasWidth;
        public int CanvasHeight { get; } = canvasHeight;
        public int ContentWidth { get; } = contentWidth;
        public int ContentHeight { get; } = contentHeight;
        public int Fps { get; } = fps;
        public double DurationSeconds { get; } = durationSeconds;
    }

    internal static class SkiaClipCompositorSketch
    {
        /// <summary>
        /// Full per-clip chain: content (already at native resolution) ->
        /// PreTransform effects -> Modulate -> warp-draw onto canvas ->
        /// PostTransform effects. Order matches ClipVideoChain.Build's
        /// documented fixed order exactly; only the transform stage and the
        /// sizes it operates at have changed.
        ///
        /// Draws directly onto `canvas` (caller owns layering/z-order across
        /// clips) rather than returning a label, since there's no filter
        /// graph to wire labels through anymore.
        /// </summary>
        public static void Composite(
            SKCanvas canvas,
            SKImage nativeContent,
            Clip clip,
            ClipTransform literalTransform,
            int nativeWidth, int nativeHeight,
            SkClipChainContext context,
            SkSurfacePool pool,
            bool modulateAlreadyApplied = false,
            bool preTransformEffectsBaked = false)
        {
            (int contentWidth, int contentHeight) = (context.ContentWidth, context.ContentHeight);

            // 1. Rasterize content down to its ComputeContentSize dims.
            //    Ordinary Skia resize (mipmapped/linear), no bespoke math
            //    needed here unlike the warp itself.
            using SKImage sized = ResizeContent(nativeContent, contentWidth, contentHeight, pool);

            // 2. PreTransform effects, at content resolution (not native,
            //    not canvas) — decided in-conversation: downscale first,
            //    then effects, since effects are strictly cheaper on the
            //    smaller raster and there's no quality argument for native
            //    res first.
            SKImage preEffects = preTransformEffectsBaked
                ? sized
                : ClipEffectsSk.ApplyStage(clip.Effects, EffectStage.PreTransform, sized, context, pool);

            // 3. Modulate — per-channel multiply, same skip-if-identity rule
            //    as before, now a ColorFilter instead of colorchannelmixer.
            SKImage modulated = modulateAlreadyApplied
                ? preEffects
                : ApplyModulateSk(clip, preEffects, pool);

            // 4. Warp-draw. No identity fast path needed anymore in the old
            //    sense — ClipVideoChain's identity fast path existed to skip
            //    an expensive ffmpeg perspective invocation; SKCanvas.Concat
            //    with an identity-ish SKMatrix should cost nothing extra, so
            //    there's nothing to special-case. (Re-confirm once real
            //    profiling exists — flagging the assumption, not asserting
            //    it's free.)
            //
            //    PostTransform effects (item 5, now real) need to operate on
            //    what's ACTUALLY ON SCREEN after the warp, not on `modulated`
            //    directly — DropShadow/Blur read the drawn silhouette. So
            //    when any are present, warp-draw into an offscreen canvas-
            //    sized surface first, run the PostTransform chain on THAT,
            //    then composite the filtered result onto the real canvas
            //    with a plain (unfiltered) draw. When none are present, skip
            //    the extra surface and draw straight onto `canvas` as
            //    before — no reason to pay for a round-trip nothing uses.
            SKMatrix matrix = SkTransformExpressions.BuildLiteralMatrix(
                literalTransform, nativeWidth, nativeHeight,
                context.CanvasWidth, context.CanvasHeight,
                contentWidth, contentHeight);

            bool hasPostTransformEffects = clip.Effects != null &&
                clip.Effects.Any(e => e.Enabled && e.Stage == EffectStage.PostTransform);

            if (!hasPostTransformEffects)
            {
                DrawWarped(canvas, modulated, matrix);
            }
            else
            {
                SKSurface layer = pool.Rent(context.CanvasWidth, context.CanvasHeight);
                SKImage warped;
                try
                {
                    layer.Canvas.Clear(SKColors.Transparent);
                    DrawWarped(layer.Canvas, modulated, matrix);
                    warped = layer.Snapshot();
                }
                finally
                {
                    pool.Return(layer, context.CanvasWidth, context.CanvasHeight);
                }

                using (warped)
                using (SKImage postEffects = ClipEffectsSk.ApplyStage(
                    clip.Effects, EffectStage.PostTransform, warped, context, pool))
                {
                    canvas.DrawImage(postEffects, 0, 0);
                }
            }

            if (!ReferenceEquals(modulated, sized) && !ReferenceEquals(modulated, preEffects))
                modulated.Dispose();
            if (!ReferenceEquals(preEffects, sized))
                preEffects.Dispose();
        }

        private static void DrawWarped(SKCanvas canvas, SKImage content, SKMatrix matrix)
        {
            canvas.Save();
            canvas.Concat(in matrix);
            using (var paint = new SKPaint { IsAntialias = true })
            {
                var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
                canvas.DrawImage(content, 0, 0, sampling, paint);
            }
            canvas.Restore();
        }

        private static SKImage ResizeContent(SKImage source, int width, int height, SkSurfacePool pool)
        {
            SKSurface surface = pool.Rent(width, height);
            try
            {
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
                var dest = new SKRect(0, 0, width, height);
                canvas.DrawImage(source, dest, sampling);
                return surface.Snapshot();
            }
            finally
            {
                pool.Return(surface, width, height);
            }
        }

        private static SKImage ApplyModulateSk(Clip clip, SKImage source, SkSurfacePool pool)
        {
            var colour = clip.Modulate;
            if (colour.Red == 255 && colour.Green == 255 && colour.Blue == 255 && colour.Alpha == 255)
                return source;

            SKSurface surface = pool.Rent(source.Width, source.Height);
            try
            {
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);

                float[] matrix =
                {
                    colour.Red / 255f, 0, 0, 0, 0,
                    0, colour.Green / 255f, 0, 0, 0,
                    0, 0, colour.Blue / 255f, 0, 0,
                    0, 0, 0, colour.Alpha / 255f, 0,
                };

                using var paint = new SKPaint { ColorFilter = SKColorFilter.CreateColorMatrix(matrix) };
                canvas.DrawImage(source, 0, 0, paint);
                return surface.Snapshot();
            }
            finally
            {
                pool.Return(surface, source.Width, source.Height);
            }
        }
    }
}
