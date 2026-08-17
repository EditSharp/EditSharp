using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using EditSharp.Components.Effects;

namespace EditSharp.Render
{
    /// <summary>
    /// Items 5-6: BlurEffect/DropShadowEffect/RoundedCornersEffect all
    /// ported to native Skia (SKImageFilter for the first two, a clip path
    /// for the third). Replaces ClipEffects' entire ffmpeg-side
    /// implementation of all three — split/fillborders/pad/crop chains,
    /// the rasterized mask-file cache, all of it.
    /// </summary>
    internal static class ClipEffectsSk
    {
        /// <summary>
        /// Runs every enabled effect assigned to the given stage, in list
        /// order — same contract as ClipEffects.ApplyStage. `context`
        /// supplies CanvasWidth/Height for normalization (Radius/Offset/
        /// BlurRadius are all normalized against the CANVAS, never the
        /// raster actually being filtered — same rule for PreTransform
        /// effects operating on the smaller content raster as for
        /// PostTransform ones operating on the canvas-sized buffer, exactly
        /// per the canvas-size-agnostic principle: normalized values
        /// resolve against canvas size at the point of use, not against
        /// whatever buffer happens to be in hand).
        /// </summary>
        public static SKImage ApplyStage(
            List<Effect> effects, EffectStage stage, SKImage input, SkClipChainContext context)
        {
            if (effects == null || effects.Count == 0) return input;

            SKImage current = input;

            foreach (Effect effect in effects.Where(e => e.Enabled && e.Stage == stage))
            {
                SKImage next = effect switch
                {
                    BlurEffect blur => ApplyBlur(blur, current, context),
                    DropShadowEffect shadow => ApplyDropShadow(shadow, current, context),
                    RoundedCornersEffect rounded => ApplyRoundedCorners(rounded, current),
                    _ => throw new NotSupportedException(
                        $"Unknown Effect subtype: {effect.GetType().Name}"),
                };

                if (!ReferenceEquals(next, current) && !ReferenceEquals(current, input))
                    current.Dispose();

                current = next;
            }

            return current;
        }

        /// <summary>
        /// Item 6: rounds `input`'s own corners via a native antialiased
        /// Skia clip, replacing the whole old rasterize-mask-file +
        /// split/alphaextract/blend=multiply/alphamerge chain.
        ///
        /// This is deliberately simpler than the old code, not just a
        /// mechanical port, because a structural reason for the old
        /// complexity has disappeared: ClipEffects.ApplyRoundedCorners had
        /// to separately compute a mask rect from `context.Placement`
        /// (PreTransform) or the full WorkRect frame (PostTransform)
        /// because the ffmpeg buffer it operated on was often LARGER than
        /// the content itself (frame padding, borders). In this pipeline
        /// `input` IS exactly the content's own tight raster at whichever
        /// stage is running — ComputeContentSize-sized at PreTransform,
        /// canvas-sized at PostTransform, with no border in either case (see
        /// item 3/4 notes on TransparentBorderPixels/WorkRect both being
        /// dropped). So there's no separate rect to derive — the mask is
        /// just `input`'s own bounds, always. Corner radius uses the same
        /// 0-1-fraction-of-min(width,height)/2 convention as the old
        /// RasterizeRoundedRectMask, unchanged, so existing effect values
        /// on existing blueprints still mean the same thing.
        ///
        /// Preserves the old semantics for PostTransform specifically
        /// too — it rounds the corners of the (canvas-sized) FRAME, not
        /// the actual warped content silhouette, exactly like the old
        /// code's own documented caveat ("assigning this effect to
        /// PostTransform on a rotated or warped clip rounds the corners of
        /// the frame rather than of the content"). Not fixed here — that
        /// caveat is a property of what PostTransform rounding MEANS, not
        /// an artifact of the ffmpeg implementation, so it carries over
        /// unchanged rather than being treated as a bug to solve.
        ///
        /// ClipRoundRect with antiAlias:true gives partial pixel coverage
        /// at the boundary the same way the old mask's SKPaint.IsAntialias
        /// did — a Skia AA clip IS a coverage-based alpha multiply, which is
        /// exactly the "multiply into existing alpha, don't replace it"
        /// property the old code called out as load-bearing (content's own
        /// transparency — a logo, glyph gaps — survives). Nothing extra
        /// needed to preserve that property; it falls out of clipping being
        /// implemented that way rather than needing to be arranged for.
        /// </summary>
        private static SKImage ApplyRoundedCorners(RoundedCornersEffect effect, SKImage input)
        {
            float radius = Math.Clamp(effect.Radius, 0f, 1f) * (Math.Min(input.Width, input.Height) / 2f);

            using var surface = SKSurface.Create(
                new SKImageInfo(input.Width, input.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            SKCanvas canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            var roundRect = new SKRoundRect(new SKRect(0, 0, input.Width, input.Height), radius, radius);

            canvas.Save();
            canvas.ClipRoundRect(roundRect, SKClipOperation.Intersect, antialias: true);
            canvas.DrawImage(input, 0, 0);
            canvas.Restore();

            return surface.Snapshot();
        }

        /// <summary>
        /// Gaussian blur via SKImageFilter.CreateBlur. Radius is normalized
        /// against CANVAS width, same convention as the old gblur sigma —
        /// unchanged so existing blueprints render the same strength.
        ///
        /// TileMode.Decal (outside-source = transparent) rather than Clamp:
        /// this is the Skia equivalent of the old code's deliberate choice
        /// to smear the colour plane's border before blurring rather than
        /// let it clamp — except here it's the DEFAULT correct behaviour,
        /// not a workaround, because the image is premultiplied. A
        /// transparent pixel's RGB is already zeroed by premultiplication,
        /// so blurring across the edge attenuates colour and alpha together
        /// in the same proportion, instead of dragging real colour toward
        /// black the way straight-alpha RGBA does. Nothing extra to do.
        /// </summary>
        private static SKImage ApplyBlur(BlurEffect effect, SKImage input, SkClipChainContext context)
        {
            float sigma = (float)Math.Clamp(effect.Radius * context.CanvasWidth, 0.1, 1024.0);

            using var filter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Decal);
            using var paint = new SKPaint { ImageFilter = filter };

            return DrawFiltered(input, paint, input.Width, input.Height);
        }

        /// <summary>
        /// SKImageFilter.CreateDropShadow produces original-plus-shadow in
        /// one filtered draw, replacing the old chain's whole
        /// pad/overlay/gblur/crop/overlay sequence — including the margin
        /// math that existed purely to give ffmpeg's `gblur` real pixels to
        /// spread into near a canvas edge. Skia's filter is evaluated in an
        /// unbounded conceptual plane and only clipped to the destination
        /// surface, so there is no analogous truncation to guard against.
        ///
        /// Opacity folds into the shadow colour's alpha channel, since
        /// CreateDropShadow takes one SKColor rather than a separate
        /// opacity multiplier.
        /// </summary>
        private static SKImage ApplyDropShadow(DropShadowEffect effect, SKImage input, SkClipChainContext context)
        {
            float sigma = (float)Math.Clamp(effect.BlurRadius * context.CanvasWidth, 0.1, 1024.0);

            // Offset is normalized like Position — a fraction of half the
            // canvas — and Y is up, so a positive Y offset lifts the shadow.
            float dx = effect.Offset.X * context.CanvasWidth / 2f;
            float dy = -effect.Offset.Y * context.CanvasHeight / 2f;

            byte alpha = (byte)Math.Round(Math.Clamp(effect.Opacity, 0f, 1f) * 255f);

            var shadowColor = new SKColor(effect.Color.Red, effect.Color.Green, effect.Color.Blue, alpha);

            using var filter = SKImageFilter.CreateDropShadow(dx, dy, sigma, sigma, shadowColor);
            using var paint = new SKPaint { ImageFilter = filter };

            return DrawFiltered(input, paint, input.Width, input.Height);
        }

        /// <summary>
        /// Draws `input` through `paint` (carrying an ImageFilter) into a
        /// fresh surface and snapshots the result. Same per-call allocation
        /// caveat already flagged for ResizeContent/ApplyModulateSk in item
        /// 4 — a real cost worth revisiting once profiling exists, not
        /// addressed here.
        /// </summary>
        private static SKImage DrawFiltered(SKImage input, SKPaint paint, int width, int height)
        {
            using var surface = SKSurface.Create(
                new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            SKCanvas canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.DrawImage(input, 0, 0, paint);
            return surface.Snapshot();
        }
    }
}
