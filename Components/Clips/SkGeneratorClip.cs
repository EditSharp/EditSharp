using System;
using SkiaSharp;
using EditSharp.Components;

namespace EditSharp.Render
{
    /// <summary>
    /// Item 9: GeneratorClip was already the trivial case the checklist
    /// expected — FrameFilterChain.GeneratorColourAt already resolves the
    /// ColorIn/ColorMain/ColorOut ramp to a single literal SKColor in C#
    /// per frame (that logic predates this whole migration and was never
    /// ffmpeg-dependent; it explicitly exists BECAUSE building the ramp as
    /// an xfade animation was the wrong tool per-frame). The only ffmpeg
    /// thing happening at all was the LAST step — formatting that resolved
    /// colour as a `color=0xRRGGBB@a:size=WxH` filter source. That step is
    /// replaced here; nothing about the colour math changes.
    ///
    /// SIMPLIFICATION, not just a port: the old code built the solid fill
    /// at full canvas resolution (nativeWidth/Height = canvasWidth/Height)
    /// because that's what ffmpeg's `color` source filter needs regardless
    /// of how the pixels get used downstream. A flat colour has no texture
    /// to preserve at any resolution — scaling a 1x1 image up to any target
    /// size via linear/mipmap sampling reproduces the exact same flat
    /// colour losslessly, since there's only one texel to sample. So the
    /// actual raster this returns is 1x1, not canvas-sized — SkiaClipCompositorSketch.Composite's
    /// existing ResizeContent step stretches it to whatever ComputeContentSize
    /// decided, with zero special-casing needed in the compositor for
    /// generators specifically. This is a real cost reduction (no canvas-
    /// sized surface allocation/fill per generator clip per frame), not
    /// just a smaller number for its own sake.
    ///
    /// nativeWidth/nativeHeight passed to TransformExpressions (aspect-fit
    /// math) still resolve to canvasWidth/canvasHeight, unchanged — those
    /// are independent of the actual pixel buffer's own dimensions, and
    /// this is what makes an untransformed generator fill the canvas
    /// exactly (matching aspect ratio) rather than letterboxing against an
    /// arbitrary 1x1 "native aspect ratio" of 1:1.
    /// </summary>
    internal static class SkGeneratorClip
    {
        /// <summary>
        /// The 1x1 solid-fill "native content" for this generator at this
        /// clip-relative instant. Caller passes canvasWidth/canvasHeight as
        /// this clip's nativeWidth/nativeHeight to the rest of the chain —
        /// see class remarks for why that's still correct despite the
        /// actual image being 1x1.
        /// </summary>
        public static SKImage Render(GeneratorClip clip, double clipSeconds, SkSurfacePool pool)
        {
            SKColor colour = ColourAt(clip, clipSeconds);

            SKSurface surface = pool.Rent(1, 1);
            try
            {
                surface.Canvas.Clear(colour);
                return surface.Snapshot();
            }
            finally
            {
                pool.Return(surface, 1, 1);
            }
        }

        /// <summary>
        /// Direct port of FrameFilterChain.GeneratorColourAt, unchanged
        /// math — only the return type changes (plain SKColor instead of
        /// the ffmpeg-hex-formatting SKColorLike, which has no reason to
        /// exist once nothing downstream needs an ffmpeg colour literal).
        /// </summary>
        public static SKColor ColourAt(GeneratorClip clip, double seconds)
        {
            double total = clip.Duration.TotalSeconds;

            double fadeIn = clip.ColorIn is { } inRamp
                ? Math.Clamp(inRamp.Item2.TotalSeconds, 0, total)
                : 0;

            if (fadeIn > 0 && seconds < fadeIn)
                return Lerp(clip.ColorIn!.Value.Item1, clip.ColorMain, seconds / fadeIn);

            double fadeOut = clip.ColorOut is { } outRamp
                ? Math.Clamp(outRamp.Item2.TotalSeconds, 0, total - fadeIn)
                : 0;

            if (fadeOut > 0 && seconds > total - fadeOut)
            {
                double u = (seconds - (total - fadeOut)) / fadeOut;
                return Lerp(clip.ColorMain, clip.ColorOut!.Value.Item1, u);
            }

            return clip.ColorMain;
        }

        private static SKColor Lerp(SKColor from, SKColor to, double u)
        {
            u = Math.Clamp(u, 0.0, 1.0);

            return new SKColor(
                (byte)Math.Round(from.Red + ((to.Red - from.Red) * u)),
                (byte)Math.Round(from.Green + ((to.Green - from.Green) * u)),
                (byte)Math.Round(from.Blue + ((to.Blue - from.Blue) * u)),
                (byte)Math.Round(from.Alpha + ((to.Alpha - from.Alpha) * u)));
        }
    }
}
