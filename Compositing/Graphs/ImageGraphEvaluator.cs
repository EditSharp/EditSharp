using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SkiaSharp;
using EditSharp.Components.Channels;
using EditSharp.Components.Nodes;
using EditSharp.Components.Nodes.Effects;
using EditSharp.Components.Nodes.Math;
using EditSharp.Components.Clips;
using EditSharp.Compositing.Gpu;
using EditSharp.Compositing.Transforms;


namespace EditSharp.Compositing.Graphs
{
    /// <summary>
    /// A REAL topological-order graph evaluator (Kahn's algorithm, via the
    /// shared GraphTopology.Order) for the Image/Mask domain — the
    /// video-side counterpart to AudioGraphEvaluator.
    ///
    /// "CLIPS ARE GRAPHS" REWRITE — this is the file most reshaped by that
    /// change:
    ///   - There is no longer a single fixed ImageSourceNode anchor seeded
    ///     with one externally-decoded `content` image. A graph can have
    ///     ANY NUMBER of InputNodes (VideoSourceNode/TextInputNode/
    ///     ColorGeneratorInputNode/NoiseInputNode/TimelineVideoInputNode),
    ///     each already resolved to a raw SKImage by the caller
    ///     (ClipContentSource — see its own remarks on per-InputNode-type
    ///     dispatch) and handed in here as `resolvedInputs`, keyed by each
    ///     InputNode's own Id. This evaluator seeds the (NodeId,"Image")
    ///     cache from that dictionary for every InputNode encountered in
    ///     topological order, then walks the rest of the graph exactly as
    ///     before.
    ///   - Resize/Tint/Warp are no longer driven externally by
    ///     ClipCompositor as a fixed pre-graph sequence — TintNode
    ///     (what replaced the old flat Modulate property) and TransformNode
    ///     (which now carries its OWN ClipTransform data, since there's no
    ///     more VisualClip to hold it) are ordinary dispatch cases in this
    ///     evaluator's own switch, exactly like Blur/DropShadow/etc always
    ///     were. TransformNode in particular now computes its own content
    ///     size (TransformProjection.ComputeContentSize) from whatever
    ///     image is ACTUALLY upstream of it at that point in the graph,
    ///     rather than a single precomputed per-clip content size — the
    ///     correct generalization once a graph can have more than one
    ///     TransformNode (e.g. one per branch before a MergeNode).
    ///   - ValueConstantNode/MathNode (see EditSharp.Components.Nodes.Math)
    ///     are visited in topological order like any other node but
    ///     contribute nothing to the Image cache directly — they're resolved
    ///     ON DEMAND by ValueGraphEvaluator, walking backward from whichever
    ///     node's optional Value input (MergeNode's "MixModulation") is
    ///     actually connected, at the moment that node is dispatched. This
    ///     evaluator just needs to not choke on visiting them.
    ///
    /// MASKS: unchanged — alpha-only convention, see ApplyMask/
    /// RenderShapeMask/ExtractMask/CombineMasks below.
    /// </summary>
    internal static class ImageGraphEvaluator
    {
        /// <summary>
        /// Runs the whole graph for one clip at one instant. `resolvedInputs`
        /// must contain one entry per InputNode in `graph.Nodes` (keyed by
        /// that node's own Id) — see ClipContentSource.GetContent. Returns
        /// the ImageOutputNode's resolved input image; the caller keeps
        /// ownership of every image in `resolvedInputs`, every OTHER
        /// intermediate image created along the way is disposed before
        /// returning, except the one actually returned.
        /// </summary>
        public static SKImage Evaluate(
            Graph graph,
            IReadOnlyDictionary<Guid, SKImage> resolvedInputs,
            TimeSpan clipRelativeTime,
            SkClipChainContext context,
            SurfacePool pool)
        {
            List<Node> order = GraphTopology.Order(graph);
 
            var images = new Dictionary<(Guid, string), SKImage>();
            var masks = new Dictionary<(Guid, string), SKImage>();
            var owned = new List<SKImage>();
 
            SKImage? result = null;
 
            foreach (Node node in order)
            {
                if (node is InputNode)
                {
                    if (!resolvedInputs.TryGetValue(node.Id, out SKImage? content))
                        throw new InvalidOperationException(
                            $"No resolved content supplied for {node.GetType().Name} ({node.Id}).");
 
                    images[(node.Id, "Image")] = content;
                    continue;
                }
 
                if (ReferenceEquals(node, graph.OutputNode))
                {
                    result = RequireImage(graph, node, "Image", images);
                    continue;
                }
 
                switch (node)
                {
                    case TintNode tint:
                    {
                        SKImage upstream = RequireImage(graph, node, "Image", images);
                        SKColor colour = tint.Color.Evaluate(clipRelativeTime);
 
                        if (!tint.Enabled || IsOpaqueWhite(colour))
                        {
                            images[(node.Id, "Image")] = upstream;
                            break;
                        }
 
                        SKImage tinted = ApplyTint(upstream, colour, pool);
                        owned.Add(tinted);
                        images[(node.Id, "Image")] = tinted;
                        break;
                    }
 
                    case TransformNode transformNode:
                    {
                        SKImage upstream = RequireImage(graph, node, "Image", images);
 
                        if (!transformNode.Enabled)
                        {
                            images[(node.Id, "Image")] = upstream;
                            break;
                        }
 
                        int nativeWidth = upstream.Width;
                        int nativeHeight = upstream.Height;
 
                        ResolvedTransform literalTransform = transformNode.Transform.Evaluate(clipRelativeTime);
 
                        (int contentWidth, int contentHeight) = TransformProjection.ComputeContentSize(
                            transformNode.Transform, nativeWidth, nativeHeight,
                            context.CanvasWidth, context.CanvasHeight);
 
                        SKImage sized = TransformMatrix.Resize(upstream, contentWidth, contentHeight, pool);
                        if (!ReferenceEquals(sized, upstream)) owned.Add(sized);
 
                        SKMatrix matrix = TransformMatrix.BuildLiteralMatrix(
                            literalTransform, nativeWidth, nativeHeight,
                            context.CanvasWidth, context.CanvasHeight, contentWidth, contentHeight);
 
                        SKSurface warpSurface = pool.Rent(context.CanvasWidth, context.CanvasHeight);
                        SKImage warped;
                        try
                        {
                            warpSurface.Canvas.Clear(SKColors.Transparent);
                            TransformMatrix.DrawWarped(warpSurface.Canvas, sized, matrix);
                            warped = warpSurface.Snapshot();
                        }
                        finally
                        {
                            pool.Return(warpSurface, context.CanvasWidth, context.CanvasHeight);
                        }
 
                        owned.Add(warped);
                        images[(node.Id, "Image")] = warped;
                        break;
                    }
 
                    case BlurNode blur:
                    {
                        SKImage upstream = RequireImage(graph, node, "Image", images);
 
                        if (!blur.Enabled) { images[(node.Id, "Image")] = upstream; break; }
 
                        SKImage? mask = ResolveMask(graph, node, "Mask", masks, upstream, pool, owned);
                        float sigma = (float)Math.Clamp(
                            blur.Radius.Evaluate(clipRelativeTime) * context.CanvasWidth, 0.1, 1024.0);
 
                        // NOTE: the pre-mask filtered image is tracked in
                        // `owned` immediately, separately from the (possibly
                        // different) post-mask image — previously the bare
                        // filtered image was silently dropped when a Mask
                        // was connected (ApplyMask's return value overwrote
                        // the only reference to it before it was ever added
                        // to `owned`), leaking one SKImage per frame for
                        // every Blur node with a connected Mask input.
                        SKImage blurred = ApplyBlur(upstream, sigma, pool);
                        owned.Add(blurred);
                        SKImage result2 = mask != null ? ApplyMask(blurred, mask, pool, owned) : blurred;
                        images[(node.Id, "Image")] = result2;
                        break;
                    }
 
                    case DropShadowNode shadow:
                    {
                        SKImage upstream = RequireImage(graph, node, "Image", images);
 
                        if (!shadow.Enabled) { images[(node.Id, "Image")] = upstream; break; }
 
                        SKImage? mask = ResolveMask(graph, node, "Mask", masks, upstream, pool, owned);
 
                        // Same leak/fix as BlurNode above: track the
                        // pre-mask shadowed image in `owned` right away
                        // instead of only tracking whichever image happens
                        // to survive the (possible) mask reassignment.
                        SKImage shadowed = ApplyDropShadow(shadow, upstream, clipRelativeTime, context, pool);
                        owned.Add(shadowed);
                        SKImage result2 = mask != null ? ApplyMask(shadowed, mask, pool, owned) : shadowed;
                        images[(node.Id, "Image")] = result2;
                        break;
                    }
 
                    case RoundedCornersNode rounded:
                    {
                        SKImage upstream = RequireImage(graph, node, "Image", images);
 
                        if (!rounded.Enabled) { images[(node.Id, "Image")] = upstream; break; }
 
                        float radius = rounded.Radius.Evaluate(clipRelativeTime);
                        SKImage result2 = ApplyRoundedCorners(upstream, radius, pool);
                        owned.Add(result2);
                        images[(node.Id, "Image")] = result2;
                        break;
                    }
 
                    case MergeNode merge:
                    {
                        SKImage a = RequireImage(graph, node, "A", images);
                        SKImage b = RequireImage(graph, node, "B", images);
 
                        float baseMix = Math.Clamp(merge.Mix.Evaluate(clipRelativeTime), 0f, 1f);
 
                        //optional Value modulation — see MergeNode's own
                        //remarks: multiplies against Mix's own keyframed
                        //value rather than replacing it, when connected
                        float? modulation = ValueGraphEvaluator.TryEvaluateConnectedInput(
                            graph, merge, "MixModulation", clipRelativeTime);
 
                        float mix = Math.Clamp(modulation.HasValue ? baseMix * modulation.Value : baseMix, 0f, 1f);
 
                        SKImage result2 = ApplyMerge(a, b, merge.BlendMode, mix, pool);
                        owned.Add(result2);
                        images[(node.Id, "Result")] = result2;
                        break;
                    }
 
                    case ShapeMaskNode shape:
                    {
                        SKImage mask = RenderShapeMask(shape, clipRelativeTime, context, pool);
                        owned.Add(mask);
                        masks[(node.Id, "Mask")] = mask;
                        break;
                    }
 
                    case ImageToMaskNode toMask:
                    {
                        SKImage upstream = RequireImage(graph, node, "Image", images);
                        SKImage mask = ExtractMask(upstream, toMask.Channel, pool);
                        owned.Add(mask);
                        masks[(node.Id, "Mask")] = mask;
                        break;
                    }
 
                    case MaskCombineNode combine:
                    {
                        SKImage a = RequireMask(graph, node, "A", masks);
                        SKImage b = RequireMask(graph, node, "B", masks);
                        SKImage result2 = CombineMasks(a, b, combine.Mode, pool);
                        owned.Add(result2);
                        masks[(node.Id, "Result")] = result2;
                        break;
                    }
 
                    //Value-domain nodes (ValueConstantNode/MathNode) carry no
                    //Image output at all — they're resolved on demand by
                    //ValueGraphEvaluator wherever a consuming node's optional
                    //Value input is actually connected (see MergeNode above),
                    //not through this cache. Nothing to do here but move on.
                    case ValueConstantNode:
                    case MathNode:
                        break;
 
                    default:
                        throw new NotSupportedException(
                            $"ImageGraphEvaluator has no dispatch for {node.GetType().Name}.");
                }
            }
 
            SKImage final = result ?? throw new InvalidOperationException(
                "Graph's ImageOutputNode has no incoming connection.");
 
            foreach (SKImage image in owned)
            {
                if (!ReferenceEquals(image, final) && !IsResolvedInput(image, resolvedInputs)) image.Dispose();
            }
 
            return final;
        }
 
        private static bool IsResolvedInput(SKImage image, IReadOnlyDictionary<Guid, SKImage> resolvedInputs)
        {
            foreach (SKImage input in resolvedInputs.Values)
            {
                if (ReferenceEquals(input, image)) return true;
            }
            return false;
        }
 
        private static bool IsOpaqueWhite(SKColor colour) =>
            colour.Red == 255 && colour.Green == 255 && colour.Blue == 255 && colour.Alpha == 255;
 
        // -----------------------------------------------------------
        // Port resolution
        // -----------------------------------------------------------
 
        private static SKImage? ResolveImage(
            Graph graph, Node node, string portName, Dictionary<(Guid, string), SKImage> cache)
        {
            Connection? c = graph.Connections.FirstOrDefault(x => x.ToNodeId == node.Id && x.ToPort == portName);
            if (c == null) return null;
            return cache.TryGetValue((c.FromNodeId, c.FromPort), out SKImage? img) ? img : null;
        }
 
        private static SKImage RequireImage(
            Graph graph, Node node, string portName, Dictionary<(Guid, string), SKImage> cache) =>
            ResolveImage(graph, node, portName, cache)
            ?? throw new InvalidOperationException(
                $"{node.GetType().Name}'s '{portName}' input has no incoming connection.");
 
        private static SKImage? ResolveMaskRaw(
            Graph graph, Node node, string portName, Dictionary<(Guid, string), SKImage> cache)
        {
            Connection? c = graph.Connections.FirstOrDefault(x => x.ToNodeId == node.Id && x.ToPort == portName);
            if (c == null) return null;
            return cache.TryGetValue((c.FromNodeId, c.FromPort), out SKImage? img) ? img : null;
        }
 
        private static SKImage RequireMask(
            Graph graph, Node node, string portName, Dictionary<(Guid, string), SKImage> cache) =>
            ResolveMaskRaw(graph, node, portName, cache)
            ?? throw new InvalidOperationException(
                $"{node.GetType().Name}'s '{portName}' mask input has no incoming connection.");
 
        private static SKImage? ResolveMask(
            Graph graph, Node node, string portName, Dictionary<(Guid, string), SKImage> cache,
            SKImage target, SurfacePool pool, List<SKImage> owned)
        {
            SKImage? raw = ResolveMaskRaw(graph, node, portName, cache);
            if (raw == null) return null;
            if (raw.Width == target.Width && raw.Height == target.Height) return raw;
 
            SKImage resized = TransformMatrix.Resize(raw, target.Width, target.Height, pool);
            if (!ReferenceEquals(resized, raw)) owned.Add(resized);
            return resized;
        }
 
        // -----------------------------------------------------------
        // Tint (formerly the externally-applied Modulate step)
        // -----------------------------------------------------------
 
        private static SKImage ApplyTint(SKImage source, SKColor colour, SurfacePool pool)
        {
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
 
        // -----------------------------------------------------------
        // Image filter nodes
        // -----------------------------------------------------------
 
        private static SKImage ApplyBlur(SKImage input, float sigma, SurfacePool pool)
        {
            using var filter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Decal);
            using var paint = new SKPaint { ImageFilter = filter };
            return DrawFiltered(input, paint, input.Width, input.Height, pool);
        }
 
        private static SKImage ApplyDropShadow(
            DropShadowNode shadow, SKImage input, TimeSpan time, SkClipChainContext context, SurfacePool pool)
        {
            float sigma = (float)Math.Clamp(shadow.Blur.Evaluate(time) * context.CanvasWidth, 0.1, 1024.0);
 
            Vector2 offset = shadow.Offset.Evaluate(time);
            float dx = offset.X * context.CanvasWidth / 2f;
            float dy = -offset.Y * context.CanvasHeight / 2f;
 
            SKColor colour = shadow.Color.Evaluate(time);
 
            using var filter = SKImageFilter.CreateDropShadow(dx, dy, sigma, sigma, colour);
            using var paint = new SKPaint { ImageFilter = filter };
            return DrawFiltered(input, paint, input.Width, input.Height, pool);
        }
 
        private static SKImage ApplyRoundedCorners(SKImage input, float radiusFraction, SurfacePool pool)
        {
            float radius = Math.Clamp(radiusFraction, 0f, 1f) * (Math.Min(input.Width, input.Height) / 2f);
 
            SKSurface surface = pool.Rent(input.Width, input.Height);
            try
            {
                SKCanvas canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
 
                var roundRect = new SKRoundRect(new SKRect(0, 0, input.Width, input.Height), radius, radius);
 
                canvas.Save();
                canvas.ClipRoundRect(roundRect, SKClipOperation.Intersect, antialias: true);
                canvas.DrawImage(input, 0, 0);
                canvas.Restore();
 
                return surface.Snapshot();
            }
            finally
            {
                pool.Return(surface, input.Width, input.Height);
            }
        }
 
        private static SKImage ApplyMask(SKImage input, SKImage mask, SurfacePool pool, List<SKImage> owned)
        {
            SKSurface surface = pool.Rent(input.Width, input.Height);
            try
            {
                SKCanvas canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                canvas.DrawImage(input, 0, 0);
 
                using var paint = new SKPaint { BlendMode = SKBlendMode.DstIn };
                canvas.DrawImage(mask, 0, 0, paint);
 
                SKImage result = surface.Snapshot();
                owned.Add(result);
                return result;
            }
            finally
            {
                pool.Return(surface, input.Width, input.Height);
            }
        }
 
        private static SKImage ApplyMerge(SKImage a, SKImage b, ChannelBlendMode blendMode, float mix, SurfacePool pool)
        {
            int width = Math.Max(a.Width, b.Width);
            int height = Math.Max(a.Height, b.Height);
 
            SKSurface surface = pool.Rent(width, height);
            try
            {
                SKCanvas canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                canvas.DrawImage(a, 0, 0);
 
                using (var paint = new SKPaint { Color = new SKColor(255, 255, 255, (byte)Math.Round(mix * 255f)) })
                {
                    canvas.SaveLayer(paint);
                    using var blendPaint = new SKPaint { BlendMode = ChannelCompositor.ToNativeForMerge(blendMode) };
                    canvas.DrawImage(b, 0, 0, blendPaint);
                    canvas.Restore();
                }
 
                return surface.Snapshot();
            }
            finally
            {
                pool.Return(surface, width, height);
            }
        }
 
        // -----------------------------------------------------------
        // Mask-producing nodes
        // -----------------------------------------------------------
 
        private static SKImage RenderShapeMask(ShapeMaskNode shape, TimeSpan time, SkClipChainContext context, SurfacePool pool)
        {
            int width = context.CanvasWidth;
            int height = context.CanvasHeight;
 
            Vector2 position = shape.Position.Evaluate(time);
            Vector2 size = shape.Size.Evaluate(time);
            float rotation = shape.Rotation.Evaluate(time);
            float feather = Math.Max(0f, shape.Feather.Evaluate(time));
 
            float centerX = width / 2f + position.X * width / 2f;
            float centerY = height / 2f - position.Y * height / 2f;
            float halfW = Math.Abs(size.X) * width / 2f;
            float halfH = Math.Abs(size.Y) * height / 2f;
 
            SKSurface surface = pool.Rent(width, height);
            try
            {
                SKCanvas canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
 
                canvas.Save();
                canvas.Translate(centerX, centerY);
                canvas.RotateDegrees(-rotation);
 
                using (var paint = new SKPaint { Color = SKColors.White, IsAntialias = true })
                {
                    switch (shape.Shape)
                    {
                        case ShapeType.Ellipse:
                            canvas.DrawOval(new SKRect(-halfW, -halfH, halfW, halfH), paint);
                            break;
 
                        case ShapeType.Polygon when shape.PolygonPoints.Count >= 3:
                        {
                            using var path = new SKPath();
                            for (int i = 0; i < shape.PolygonPoints.Count; i++)
                            {
                                Vector2 p = shape.PolygonPoints[i].Evaluate(time);
                                float px = p.X * width / 2f;
                                float py = -p.Y * height / 2f;
                                if (i == 0) path.MoveTo(px, py); else path.LineTo(px, py);
                            }
                            path.Close();
                            canvas.DrawPath(path, paint);
                            break;
                        }
 
                        default:
                            canvas.DrawRect(new SKRect(-halfW, -halfH, halfW, halfH), paint);
                            break;
                    }
                }
 
                canvas.Restore();
 
                SKImage flat = surface.Snapshot();
 
                if (feather <= 0f) return flat;
 
                float sigma = feather * Math.Min(width, height);
                using (flat)
                using (var blurFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Decal))
                using (var blurPaint = new SKPaint { ImageFilter = blurFilter })
                {
                    return DrawFiltered(flat, blurPaint, width, height, pool);
                }
            }
            finally
            {
                pool.Return(surface, width, height);
            }
        }
 
        private static SKImage ExtractMask(SKImage input, MaskChannelSource channelSource, SurfacePool pool)
        {
            float[] matrix = channelSource == MaskChannelSource.Luma
                ? new float[]
                {
                    0, 0, 0, 0, 0,
                    0, 0, 0, 0, 0,
                    0, 0, 0, 0, 0,
                    0.2126f, 0.7152f, 0.0722f, 0, 0,
                }
                : new float[]
                {
                    0, 0, 0, 1, 0,
                    0, 0, 0, 1, 0,
                    0, 0, 0, 1, 0,
                    0, 0, 0, 1, 0,
                };
 
            using var filter = SKColorFilter.CreateColorMatrix(matrix);
            using var paint = new SKPaint { ColorFilter = filter };
            return DrawFiltered(input, paint, input.Width, input.Height, pool);
        }
 
        private static SKImage CombineMasks(SKImage a, SKImage b, MaskCombineMode mode, SurfacePool pool)
        {
            SKBlendMode blend = mode switch
            {
                MaskCombineMode.Add => SKBlendMode.Plus,
                MaskCombineMode.Intersect => SKBlendMode.Modulate,
                MaskCombineMode.Subtract => SKBlendMode.DstOut,
                _ => throw new NotSupportedException($"Unknown MaskCombineMode: {mode}"),
            };
 
            int width = Math.Max(a.Width, b.Width);
            int height = Math.Max(a.Height, b.Height);
 
            SKSurface surface = pool.Rent(width, height);
            try
            {
                SKCanvas canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                canvas.DrawImage(a, 0, 0);
 
                using var paint = new SKPaint { BlendMode = blend };
                canvas.DrawImage(b, 0, 0, paint);
 
                return surface.Snapshot();
            }
            finally
            {
                pool.Return(surface, width, height);
            }
        }
 
        // -----------------------------------------------------------
        // Shared helpers
        // -----------------------------------------------------------
 
        private static SKImage DrawFiltered(SKImage input, SKPaint paint, int width, int height, SurfacePool pool)
        {
            SKSurface surface = pool.Rent(width, height);
            try
            {
                SKCanvas canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                canvas.DrawImage(input, 0, 0, paint);
                return surface.Snapshot();
            }
            finally
            {
                pool.Return(surface, width, height);
            }
        }
    }
}
 