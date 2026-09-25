using System;
using System.Collections.Generic;
using SkiaSharp;
using EditSharp.Compositing.Gpu;
using EditSharp.Components.Clips;

namespace EditSharp.Compositing.Transforms
{
    /// <summary>Turns TransformProjection's corner positions into an SKMatrix, and draws and resizes with it.</summary>
    /// <remarks>
    /// Resize is where a large source shrinks to its on-screen size, so it samples
    /// with mipmaps: heavy minification without them shimmers in motion. DrawWarped
    /// works on content already at its target size, so it samples without them. On
    /// the D3D12 backend, generating mipmaps on the fly logs validation errors; if
    /// heavily shrunk content ever shows corruption, try Resize without mipmaps first.
    /// </remarks>
    internal static class TransformMatrix
    {
        //maps content, already rasterized at its content size, onto the quad its transform projects to
        public static SKMatrix BuildLiteralMatrix(
            ResolvedTransform transform,
            int nativeWidth, int nativeHeight,
            int canvasWidth, int canvasHeight,
            int contentWidth, int contentHeight)
        {
            //the frame is the content here, with no margin
            var placement = new TransformProjection.ContentPlacement(
                contentWidth, contentHeight, 0, 0);

            TransformProjection.Quad q = TransformProjection.ComputeQuad(
                transform, nativeWidth, nativeHeight, canvasWidth, canvasHeight,
                contentWidth, contentHeight, 0, 0, placement);

            return RectToQuad(contentWidth, contentHeight, q);
        }

        /// <summary>
        /// The general "unit-square-to-quadrilateral" homography (Heckbert),
        /// composed with a pre-scale so the SOURCE is an arbitrary WxH rect
        /// instead of the unit square.
        /// </summary>
        public static SKMatrix RectToQuad(double width, double height, TransformProjection.Quad q)
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

        //bilinear, no mipmaps: the content is already at its target size
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

        //mipmapped: this is where large sources shrink
        public static SKImage Resize(SKImage source, int width, int height, SurfacePool pool)
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
}
