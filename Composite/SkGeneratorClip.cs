using SkiaSharp;
using EditSharp.Components.Nodes.Sources.Video;
 
namespace EditSharp.Composite
{
    /// <summary>
    /// ColorGeneratorInputNode's Color is now an ordinary keyframeable
    /// Animatable&lt;SKColor&gt; — the old ColorIn/ColorMain/ColorOut
    /// fade-tuple shape is gone (a fade-in/hold/fade-out is just three
    /// keyframes on that one field now, the same simplification the rest of
    /// this schema already applied elsewhere) — so ColourAt shrinks to a
    /// direct Evaluate call, no ramp logic left to reimplement.
    ///
    /// "CLIPS ARE GRAPHS" REWRITE: renders directly at CANVAS resolution now,
    /// not a 1x1 fill. Previously, FrameClip.NativeWidth/Height was
    /// overridden to canvas size specifically for GeneratorClip/NoiseClip, so
    /// a 1x1 fill could still be treated as "native size == canvas size" by
    /// the old external resize step. That override no longer exists —
    /// TransformNode now derives its own native size directly from whatever
    /// image is ACTUALLY upstream of it (see EffectGraphEvaluatorSk's own
    /// remarks) — so this render itself has to already produce a
    /// canvas-sized image for that inference to come out the same as before.
    /// Filling a canvas-sized surface with a single colour is no more
    /// expensive in any way that matters here than filling a 1x1 one.
    /// </summary>
    internal static class SkGeneratorClip
    {
        public static SKImage Render(
            ColorGeneratorInputNode node, double clipSeconds, int canvasWidth, int canvasHeight, SkSurfacePool pool)
        {
            SKColor colour = node.Color.Evaluate(System.TimeSpan.FromSeconds(clipSeconds));
 
            SKSurface surface = pool.Rent(canvasWidth, canvasHeight);
            try
            {
                surface.Canvas.Clear(colour);
                return surface.Snapshot();
            }
            finally
            {
                pool.Return(surface, canvasWidth, canvasHeight);
            }
        }
    }
}
 