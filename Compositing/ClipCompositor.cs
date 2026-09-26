using System;
using System.Collections.Generic;
using EditSharp.Components.Nodes;
using SkiaSharp;
using EditSharp.Compositing.Gpu;
using EditSharp.Compositing.Graphs;
using EditSharp.Components.Clips;

namespace EditSharp.Compositing
{
    //what evaluating a clip's graph needs besides the graph; content sizes come from each TransformNode's own input
    internal readonly struct SkClipChainContext(int canvasWidth, int canvasHeight, Rational fps)
    {
        public int CanvasWidth { get; } = canvasWidth;
        public int CanvasHeight { get; } = canvasHeight;
        public Rational Fps { get; } = fps;
    }

    //evaluates a clip's graph with its sources' content and draws the result onto the canvas
    internal static class ClipCompositor
    {
        public static void Composite(
            SKCanvas canvas,
            Graph graph,
            IReadOnlyDictionary<Guid, SKImage> resolvedInputs,
            Time contentTime,
            SkClipChainContext context,
            SurfacePool pool)
        {
            using SKImage final = ImageGraphEvaluator.Evaluate(
                graph, resolvedInputs, contentTime, context, pool);

            canvas.DrawImage(final, 0, 0);
        }
    }
}
