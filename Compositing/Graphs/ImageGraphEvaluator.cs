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
    /// <summary>Evaluates a clip's image graph for one instant, visiting nodes in order (GraphTopology).</summary>
    /// <remarks>
    /// Every source node's image comes in already resolved, keyed by node Id; the
    /// rest of the graph (tint, transform, filters, masks, merges) runs here. A
    /// TransformNode sizes its content from the image actually upstream of it.
    /// Value nodes add nothing to the image cache: a node with a connected Value
    /// input reads it through ValueGraphEvaluator when it runs. Masks are alpha-only.
    /// </remarks>
    internal static class ImageGraphEvaluator
    {
        //runs the graph at one instant. `resolvedInputs` has an image per source node, by Id, and stays the
        //caller's; every intermediate image is disposed except the one returned, which the caller disposes
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
                //a disabled source shows nothing, like an unwired input
                if (node is InputNode)
                {
                    images[(node.Id, "Image")] = node.Enabled && resolvedInputs.TryGetValue(node.Id, out SKImage? content) ? content : Nothing;
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

                        //tracked before masking: the mask can replace `blurred`, and it must still be disposed
                        SKImage blurred = ApplyBlur(upstream, sigma, pool);
                        owned.Add(blurred);
                        SKImage result2 = mask != null ? ApplyMask(upstream, blurred, mask, pool, owned) : blurred;
                        images[(node.Id, "Image")] = result2;
                        break;
                    }

                    case DropShadowNode shadow:
                    {
                        SKImage upstream = RequireImage(graph, node, "Image", images);

                        if (!shadow.Enabled) { images[(node.Id, "Image")] = upstream; break; }

                        SKImage? mask = ResolveMask(graph, node, "Mask", masks, upstream, pool, owned);

                        //tracked before masking, as for Blur
                        SKImage shadowed = ApplyDropShadow(shadow, upstream, clipRelativeTime, context, pool);
                        owned.Add(shadowed);
                        SKImage result2 = mask != null ? ApplyMask(upstream, shadowed, mask, pool, owned) : shadowed;
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

                        if (!merge.Enabled) { images[(node.Id, "Result")] = a; break; }

                        SKImage b = RequireImage(graph, node, "B", images);

                        float baseMix = Math.Clamp(merge.Mix.Evaluate(clipRelativeTime), 0f, 1f);

                        //a connected Value input multiplies Mix's own value rather than replacing it
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
                        //a disabled mask node outputs no mask, so whatever it fed is unmasked
                        if (!shape.Enabled) break;

                        SKImage mask = RenderShapeMask(shape, clipRelativeTime, context, pool);
                        owned.Add(mask);
                        masks[(node.Id, "Mask")] = mask;
                        break;
                    }

                    case ImageToMaskNode toMask:
                    {
                        if (!toMask.Enabled) break;

                        SKImage upstream = RequireImage(graph, node, "Image", images);
                        SKImage mask = ExtractMask(upstream, toMask.Channel, pool);
                        owned.Add(mask);
                        masks[(node.Id, "Mask")] = mask;
                        break;
                    }

                    case MaskCombineNode combine:
                    {
                        if (!combine.Enabled)
                        {
                            if (ResolveMaskRaw(graph, node, "A", masks) is { } passA) masks[(node.Id, "Result")] = passA;
                            break;
                        }

                        SKImage a = RequireMask(graph, node, "A", masks);
                        SKImage b = RequireMask(graph, node, "B", masks);
                        SKImage result2 = CombineMasks(a, b, combine.Mode, pool);
                        owned.Add(result2);
                        masks[(node.Id, "Result")] = result2;
                        break;
                    }

                    //Value nodes have no image; they're read on demand where a Value input is connected
                    case ValueConstantNode:
                    case MathNode:
                        break;

                    default:
                        throw new NotSupportedException(
                            $"ImageGraphEvaluator has no dispatch for {node.GetType().Name}.");
                }
            }

            SKImage final = result ?? Nothing;

            if (ReferenceEquals(final, Nothing)) final = CreateNothing();
            else if (IsResolvedInput(final, resolvedInputs)) final = Copy(final, pool);

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

        // ---- port resolution ----

        private static SKImage? ResolveImage(
            Graph graph, Node node, string portName, Dictionary<(Guid, string), SKImage> cache)
        {
            Connection? c = graph.Connections.FirstOrDefault(x => x.ToNodeId == node.Id && x.ToPort == portName);
            if (c == null) return null;
            return cache.TryGetValue((c.FromNodeId, c.FromPort), out SKImage? img) ? img : null;
        }

        //an input that isn't wired (yet) reads as transparent rather than failing the frame
        private static readonly SKImage Nothing = CreateNothing();

        private static SKImage CreateNothing()
        {
            using var bitmap = new SKBitmap(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul);
            bitmap.Erase(SKColors.Transparent);
            return SKImage.FromBitmap(bitmap);
        }

        //the caller disposes what Evaluate returns, so an image passed straight through is handed back as a copy
        private static SKImage Copy(SKImage image, SurfacePool pool)
        {
            SKSurface surface = pool.Rent(image.Width, image.Height);
            try
            {
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.DrawImage(image, 0, 0);
                return surface.Snapshot();
            }
            finally
            {
                pool.Return(surface, image.Width, image.Height);
            }
        }

        private static SKImage RequireImage(
            Graph graph, Node node, string portName, Dictionary<(Guid, string), SKImage> cache) =>
            ResolveImage(graph, node, portName, cache) ?? Nothing;

        private static SKImage? ResolveMaskRaw(
            Graph graph, Node node, string portName, Dictionary<(Guid, string), SKImage> cache)
        {
            Connection? c = graph.Connections.FirstOrDefault(x => x.ToNodeId == node.Id && x.ToPort == portName);
            if (c == null) return null;
            return cache.TryGetValue((c.FromNodeId, c.FromPort), out SKImage? img) ? img : null;
        }

        private static SKImage RequireMask(
            Graph graph, Node node, string portName, Dictionary<(Guid, string), SKImage> cache) =>
            ResolveMaskRaw(graph, node, portName, cache) ?? Nothing;

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

        // ---- tint ----

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

        // ---- image filter nodes ----

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

        //the effect where the mask is, the original where it isn't: original * (1 - mask) + filtered * mask
        private static SKImage ApplyMask(SKImage original, SKImage filtered, SKImage mask, SurfacePool pool, List<SKImage> owned)
        {
            SKSurface surface = pool.Rent(original.Width, original.Height);
            try
            {
                SKCanvas canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                canvas.DrawImage(original, 0, 0);

                using (var outside = new SKPaint { BlendMode = SKBlendMode.DstOut })
                    canvas.DrawImage(mask, 0, 0, outside);

                using (var add = new SKPaint { BlendMode = SKBlendMode.Plus })
                {
                    canvas.SaveLayer(add);
                    canvas.DrawImage(filtered, 0, 0);
                    using var inside = new SKPaint { BlendMode = SKBlendMode.DstIn };
                    canvas.DrawImage(mask, 0, 0, inside);
                    canvas.Restore();
                }

                SKImage result = surface.Snapshot();
                owned.Add(result);
                return result;
            }
            finally
            {
                pool.Return(surface, original.Width, original.Height);
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

                //B straight onto A at Mix opacity, so the blend mode sees A underneath
                using var paint = new SKPaint { Color = new SKColor(255, 255, 255, (byte)Math.Round(mix * 255f)) };
                ChannelCompositor.ApplyBlend(paint, blendMode);
                canvas.DrawImage(b, 0, 0, paint);

                return surface.Snapshot();
            }
            finally
            {
                pool.Return(surface, width, height);
            }
        }

        // ---- mask-producing nodes ----

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
                //clockwise, like ClipTransform.Rotation
                canvas.RotateDegrees(rotation);

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

        // ---- shared helpers ----

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
