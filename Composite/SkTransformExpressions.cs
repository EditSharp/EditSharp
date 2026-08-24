using System;
using System.Collections.Generic;
using SkiaSharp;
using EditSharp.Components.Clips;
 
namespace EditSharp.Composite
{
    /// <summary>
    /// The SkiaSharp compositor's transform math. ProjectCorner/ComputeQuad
    /// (in TransformExpressions.cs) produce eight destination-corner numbers;
    /// this file turns those into a real SKMatrix.
    ///
    /// "CLIPS ARE GRAPHS" REWRITE: BuildLiteralMatrix/RectToQuad/
    /// UnitSquareToQuad are pure geometry and entirely unchanged. What
    /// changed substantially is SkiaClipCompositorSketch.Composite: resize/
    /// tint/warp are no longer a fixed external sequence this method drives
    /// — they're each their own node dispatch INSIDE EffectGraphEvaluatorSk
    /// now (TintNode, TransformNode — see SkClipEffects.cs), since a graph
    /// can have more than one InputNode/TransformNode and there is no
    /// longer a single "the content" this method could resize/tint once up
    /// front before handing off. Composite's job shrinks to: hand the
    /// clip's whole graph (with every InputNode's own resolved content) to
    /// the evaluator, and draw whatever comes out the other end.
    ///
    /// SAMPLING — MIPMAP MODE, and why the two call sites below differ.
    ///
    /// Resize() is the minification path: it is where a large source is
    /// scaled down to its on-canvas content size, and it is the only place
    /// mipmapping does real work. Heavy minification without a mip chain
    /// aliases and shimmers on motion, which is a genuine quality loss in a
    /// video compositor, so Resize keeps SKMipmapMode.Linear.
    ///
    /// DrawWarped() runs AFTER Resize, on content that has already been
    /// rasterized at its target size, so its matrix is close to 1:1 and a
    /// mip chain buys nothing — while still costing a full chain generation
    /// per image per frame. It uses SKMipmapMode.None deliberately.
    ///
    /// ONE CAVEAT WORTH KNOWING: a freshly created SKImage has no mip chain,
    /// so requesting mipmapped sampling makes Skia generate one on the fly,
    /// and on the D3D12 backend that generation path emits real validation
    /// errors (ResourceBarrierBeforeAfterMismatch / InvalidSubresourceState
    /// against Skia's internal "_Skia_CopyBaseMipMapToView" scratch
    /// resource). Those errors were investigated at length and are NOT the
    /// cause of the block-corruption bug that was chased through this file's
    /// history — that was a precision hazard in SkNoiseClip's shader. They
    /// are, however, real, and if unexplained corruption ever shows up on
    /// heavily-downscaled content specifically, switching Resize to
    /// SKMipmapMode.None is the first thing to try: it trades minification
    /// quality for avoiding that path entirely.
    /// </summary>
    internal static class SkTransformExpressions
    {
        /// <summary>
        /// Maps content (already rasterized at contentWidth x contentHeight —
        /// see TransformExpressions.ComputeContentSize) directly onto the
        /// canvas-space Quad the resolved transform projects to.
        /// </summary>
        public static SKMatrix BuildLiteralMatrix(
            ResolvedTransform transform,
            int nativeWidth, int nativeHeight,
            int canvasWidth, int canvasHeight,
            int contentWidth, int contentHeight)
        {
            // frame == content here (1:1 outset ratio) — see ComputeQuad's own remarks.
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
        /// instead of the unit square.
        /// </summary>
        public static SKMatrix RectToQuad(double width, double height, TransformExpressions.Quad q)
        {
            SKMatrix unitToQuad = UnitSquareToQuad(
                q.X0, q.Y0,
                q.X1, q.Y1,
                q.X3, q.Y3,
                q.X2, q.Y2);
 
            SKMatrix rectToUnit = SKMatrix.CreateScale(
                (float)(1.0 / width), (float)(1.0 / height));
 
            return SKMatrix.Concat(unitToQuad, rectToUnit);
        }
 
        private static SKMatrix UnitSquareToQuad(
            double x0, double y0, double x1, double y1,
            double x2, double y2, double x3, double y3)
        {
            double dx1 = x1 - x2, dx2 = x3 - x2, dx3 = x0 - x1 + x2 - x3;
            double dy1 = y1 - y2, dy2 = y3 - y2, dy3 = y0 - y1 + y2 - y3;
 
            if (dx3 == 0.0 && dy3 == 0.0)
            {
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
 
        /// <summary>
        /// Draws `content` warped by `matrix` onto `canvas`. Bilinear, not
        /// mipmapped: `content` is already at its target size by this point,
        /// so a mip chain would be pure cost. See the SAMPLING remarks.
        /// </summary>
        public static void DrawWarped(SKCanvas canvas, SKImage content, SKMatrix matrix)
        {
            canvas.Save();
            canvas.Concat(in matrix);
            using (var paint = new SKPaint { IsAntialias = true })
            {
                var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
                canvas.DrawImage(content, 0, 0, sampling, paint);
            }
            canvas.Restore();
        }
 
        /// <summary>
        /// Resizes `source` to width x height, with mipmapped sampling —
        /// this is the minification path. See the SAMPLING remarks.
        /// </summary>
        public static SKImage Resize(SKImage source, int width, int height, SkSurfacePool pool)
        {
            if (source.Width == width && source.Height == height) return source;
 
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
    }
 
    /// <summary>
    /// Everything a clip's graph evaluation needs beyond the graph itself.
    ///
    /// "CLIPS ARE GRAPHS" REWRITE: ContentWidth/ContentHeight are GONE —
    /// there is no longer a single "the content size" for a whole clip,
    /// since a graph can have more than one InputNode/TransformNode, each
    /// potentially at a different native resolution. Content size is now
    /// computed fresh at each TransformNode's own dispatch, from whatever
    /// image is actually upstream of it at that point (see
    /// EffectGraphEvaluatorSk's TransformNode case) — only CanvasWidth/
    /// CanvasHeight (what everything ultimately maps onto) is still needed
    /// globally.
    /// </summary>
    internal readonly struct SkClipChainContext(int canvasWidth, int canvasHeight, int fps, double durationSeconds)
    {
        public int CanvasWidth { get; } = canvasWidth;
        public int CanvasHeight { get; } = canvasHeight;
        public int Fps { get; } = fps;
        public double DurationSeconds { get; } = durationSeconds;
    }
 
    /// <summary>
    /// Thin driver: hand the clip's whole graph (with every InputNode's own
    /// already-resolved content) to EffectGraphEvaluatorSk, then draw
    /// whatever comes out the other end onto `canvas`. See this file's own
    /// class remarks for why this shrank so much from before this rewrite
    /// — resize/tint/warp all moved INSIDE the evaluator's own node
    /// dispatch, since there's no longer one single upfront "the content"
    /// this method could process before handing off.
    /// </summary>
    internal static class SkiaClipCompositorSketch
    {
        public static void Composite(
            SKCanvas canvas,
            VideoClip clip,
            IReadOnlyDictionary<Guid, SKImage> resolvedInputs,
            double clipSeconds,
            SkClipChainContext context,
            SkSurfacePool pool)
        {
            var clipRelativeTime = TimeSpan.FromSeconds(clipSeconds);
 
            using SKImage final = EffectGraphEvaluatorSk.Evaluate(
                clip.Graph, resolvedInputs, clipRelativeTime, context, pool);
 
            canvas.DrawImage(final, 0, 0);
        }
    }
}
 