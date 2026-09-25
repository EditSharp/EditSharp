using System;
using System.Collections.Generic;
using EditSharp.Components.Nodes;
using SkiaSharp;
using EditSharp.Compositing.Gpu;
using EditSharp.Compositing.Graphs;
using EditSharp.Components.Clips;

namespace EditSharp.Compositing
{
    /// <summary>
    /// Everything a clip's graph evaluation needs beyond the graph itself.
    ///
    /// "CLIPS ARE GRAPHS" REWRITE: ContentWidth/ContentHeight are GONE —
    /// there is no longer a single "the content size" for a whole clip,
    /// since a graph can have more than one InputNode/TransformNode, each
    /// potentially at a different native resolution. Content size is now
    /// computed fresh at each TransformNode's own dispatch, from whatever
    /// image is actually upstream of it at that point (see
    /// ImageGraphEvaluator's TransformNode case) — only CanvasWidth/
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
    /// already-resolved content) to ImageGraphEvaluator, then draw
    /// whatever comes out the other end onto `canvas`. See this file's own
    /// class remarks for why this shrank so much from before this rewrite
    /// — resize/tint/warp all moved INSIDE the evaluator's own node
    /// dispatch, since there's no longer one single upfront "the content"
    /// this method could process before handing off.
    /// </summary>
    internal static class ClipCompositor
    {
        public static void Composite(
            SKCanvas canvas,
            Graph graph,
            IReadOnlyDictionary<Guid, SKImage> resolvedInputs,
            double clipSeconds,
            SkClipChainContext context,
            SurfacePool pool)
        {
            var clipRelativeTime = TimeSpan.FromSeconds(clipSeconds);

            using SKImage final = ImageGraphEvaluator.Evaluate(
                graph, resolvedInputs, clipRelativeTime, context, pool);

            canvas.DrawImage(final, 0, 0);
        }
    }
}
