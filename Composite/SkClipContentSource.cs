using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using EditSharp.Components;
using EditSharp.Components.Clips;
using EditSharp.Components.Effects;
 
namespace EditSharp.Composite
{
    /// <summary>
    /// Resolves this frame's pixel content for EVERY InputNode in a
    /// VideoClip's graph, and owns whatever state needs to live across many
    /// frames to make that cheap — a video decoder per Video-type
    /// MediaSourceNode, a cached SKImage per Image-type MediaSourceNode or
    /// TextInputNode, a NestedTimelineRenderer per TimelineVideoInputNode.
    ///
    /// "CLIPS ARE GRAPHS" REWRITE — this is the piece that changed the most:
    ///   - GetContent used to resolve ONE image per Clip (dispatching on
    ///     Clip subtype: VideoClip/TextClip/GeneratorClip/NoiseClip). Now a
    ///     VideoClip's graph can contain any number of InputNodes, so
    ///     GetContent resolves ONE image PER INPUT NODE, keyed by that node's
    ///     own Id, and dispatches on INPUT NODE type instead of Clip subtype.
    ///     Every internal cache (video decoders, cached static images, nested
    ///     renderers) is rekeyed the same way, by node Id rather than by Clip.
    ///   - ColorGeneratorInputNode/NoiseInputNode now render directly at
    ///     canvas resolution (rather than the old 1x1-fill-then-external-resize
    ///     approach) — TransformNode derives its own "native size" from
    ///     whatever image is actually upstream of it (see EffectGraphEvaluatorSk's
    ///     own remarks), so a generator/noise InputNode's resolved image
    ///     needs to already BE the size that native-size inference should see,
    ///     which for a canvas-filling generator/noise field is canvas size —
    ///     matching the old FrameClip.NativeWidth/Height override exactly,
    ///     just moved from an external substitution into the image itself.
    ///   - TextInputNode rasterization moved HERE, lazily, cached by node Id
    ///     — RenderContentPreparation no longer prepares text up front (see
    ///     its own remarks) because there's no longer a tempFiles bag it's
    ///     natural for it to own; this class creates its own temp PNGs and
    ///     deletes them itself on Dispose.
    ///   - TimelineVideoInputNode is brand new here: recursively renders one
    ///     frame of the embedded Timeline via NestedTimelineRenderer.
    ///
    /// CALLER CONTRACT, load-bearing: GetContent must be called for a given
    /// video clip exactly once per frame it's visible on, in strictly
    /// increasing frame order, matching SkSourceDecoder.NextFrame's own
    /// contract for every Video-type MediaSourceNode in its graph — this
    /// class does not itself enforce that, it just forwards to the decoders.
    /// </summary>
    internal sealed class SkClipContentSource : IDisposable
    {
        private readonly int _fps;
        private readonly HardwareAccelerator _hwAccel;
        private readonly IReadOnlyDictionary<Guid, (int Width, int Height)> _nativeSizes;
        private readonly IReadOnlyDictionary<Guid, DecodeHwAccelPlan> _decodePlans;
        private readonly IReadOnlyDictionary<Clip, TimeSpan> _seekOffsets;
        private readonly IReadOnlyDictionary<Guid, string> _decodeSourcePaths;
 
        private readonly Dictionary<Guid, SkSourceDecoder> _videoDecoders = new();
        private readonly Dictionary<Clip, List<Guid>> _videoDecodersByClip = new();
        private readonly Dictionary<Guid, SKImage> _staticContent = new();
        private readonly Dictionary<Guid, string> _ownedTempFiles = new();
        private readonly Dictionary<Guid, NestedTimelineRenderer> _nestedRenderers = new();
 
        public SkClipContentSource(
            int fps,
            HardwareAccelerator hwAccel,
            IReadOnlyDictionary<Guid, (int Width, int Height)> nativeSizes,
            IReadOnlyDictionary<Guid, DecodeHwAccelPlan> decodePlans,
            IReadOnlyDictionary<Clip, TimeSpan>? seekOffsets = null,
            IReadOnlyDictionary<Guid, string>? decodeSourcePaths = null)
        {
            _fps = fps;
            _hwAccel = hwAccel;
            _nativeSizes = nativeSizes;
            _decodePlans = decodePlans;
            _seekOffsets = seekOffsets ?? new Dictionary<Clip, TimeSpan>();
            _decodeSourcePaths = decodeSourcePaths ?? new Dictionary<Guid, string>();
        }
 
        /// <summary>
        /// This frame's pixel content for every InputNode in `clip`'s graph,
        /// keyed by that node's own Id, each with whether the CALLER owns
        /// disposing it. Transient content (a video decoder's frame, a
        /// generator/noise field, a nested-timeline frame) is fresh every
        /// call and must be disposed by the caller once this frame's draw is
        /// done. Long-lived content (a cached static image, a rasterized text
        /// block) is owned by THIS class and must NOT be disposed by the
        /// caller — it's reused on every future frame that node is visible on.
        /// </summary>
        public IReadOnlyDictionary<Guid, (SKImage Image, bool Transient)> GetContent(
            VideoClip clip, double clipSeconds, int canvasWidth, int canvasHeight, SkSurfacePool pool)
        {
            var result = new Dictionary<Guid, (SKImage, bool)>();
 
            foreach (InputNode node in clip.Graph.Nodes.OfType<InputNode>())
            {
                result[node.Id] = node switch
                {
                    MediaSourceNode { Source.Type: SourceType.Video } media =>
                        (GetOrOpenDecoder(clip, media, canvasWidth, canvasHeight).NextFrame(), true),
 
                    MediaSourceNode { Source.Type: SourceType.Image } media =>
                        (GetOrLoadStaticImage(media.Id, media.Source.Path), false),
 
                    TextInputNode text =>
                        (GetOrRasterizeText(text, canvasWidth, canvasHeight), false),
 
                    ColorGeneratorInputNode color =>
                        (SkGeneratorClip.Render(color, clipSeconds, canvasWidth, canvasHeight, pool), true),
 
                    NoiseInputNode noise =>
                        (SkNoiseClip.Render(noise, clipSeconds, canvasWidth, canvasHeight, pool), true),
 
                    TimelineVideoInputNode embed =>
                        (GetOrCreateNestedRenderer(embed).RenderFrame(clipSeconds, canvasWidth, canvasHeight), true),
 
                    _ => throw new NotSupportedException(
                        $"SkClipContentSource has no dispatch for {node.GetType().Name}."),
                };
            }
 
            return result;
        }
 
        private SkSourceDecoder GetOrOpenDecoder(
            VideoClip clip, MediaSourceNode media, int canvasWidth, int canvasHeight)
        {
            if (_videoDecoders.TryGetValue(media.Id, out SkSourceDecoder? existing))
                return existing;
 
            (int nativeWidth, int nativeHeight) = _nativeSizes.TryGetValue(media.Id, out var size)
                ? size
                : (0, 0);
 
            if (nativeWidth <= 0 || nativeHeight <= 0)
                throw new InvalidOperationException(
                    "No native size registered for a MediaSourceNode — RenderContentPreparation must " +
                    "run (see RenderContentPreparation.PrepareContentAsync) before rendering.");
 
            //Source.Start already carries any head-trim advance (see
            //Clip.OnHeadInPointShift / MediaSourceNode.InPoint) — this IS the
            //one-time seek SkSourceDecoder's own remarks describe, paid once
            //at stream setup rather than once per frame. seekOffsets adds
            //however far INTO the clip's visible window playback is already
            //starting — zero for a full render, which reproduces the
            //original behaviour exactly. Still keyed by Clip (not node):
            //every media input on the same clip starts that same amount
            //further in, regardless of how many it has.
            double startSeconds = (media.Source.Start ?? TimeSpan.Zero).TotalSeconds;
 
            if (_seekOffsets.TryGetValue(clip, out TimeSpan extra))
                startSeconds += extra.TotalSeconds;
 
            //Decode target: the max-scale-across-the-graph's-own-keyframe-
            //range content size TransformExpressions.ComputeContentSize
            //already computes for the Skia resize step, capped at native
            //resolution per axis independently — see GraphSearchHelpers'
            //own remarks on the downstream-TransformNode approximation.
            ClipTransform transform =
                GraphSearchHelpers.FindDownstreamTransform(clip.Graph, media)?.Transform ?? new ClipTransform();
 
            (int desiredWidth, int desiredHeight) = TransformExpressions.ComputeContentSize(
                transform, nativeWidth, nativeHeight, canvasWidth, canvasHeight);
 
            int decodeWidth = Math.Min(desiredWidth, nativeWidth);
            int decodeHeight = Math.Min(desiredHeight, nativeHeight);
 
            DecodeHwAccelPlan plan = _decodePlans.TryGetValue(media.Id, out DecodeHwAccelPlan? resolvedPlan)
                ? resolvedPlan
                : DecodeHwAccelPlan.Software;
 
            //defaults to the node's own original source path when
            //RenderContentPreparation found no suitable cache entry
            string decodeSourcePath = _decodeSourcePaths.TryGetValue(media.Id, out string? overridden)
                ? overridden
                : media.Source.Path;
 
            //true only when the file actually being opened is an
            //OptimizedMediaCache entry — never the node's own original source
            bool fastOpen = decodeSourcePath != media.Source.Path;
 
            SkSourceDecoder decoder = SkSourceDecoder.Start(
                decodeSourcePath, startSeconds, _fps, decodeWidth, decodeHeight, plan, fastOpen);
 
            _videoDecoders[media.Id] = decoder;
 
            if (!_videoDecodersByClip.TryGetValue(clip, out List<Guid>? list))
                _videoDecodersByClip[clip] = list = [];
            list.Add(media.Id);
 
            return decoder;
        }
 
        private SKImage GetOrLoadStaticImage(Guid nodeId, string path)
        {
            if (_staticContent.TryGetValue(nodeId, out SKImage? cached))
                return cached;
 
            using SKData data = SKData.Create(path)
                ?? throw new InvalidOperationException($"Could not read '{path}'.");
 
            SKImage image = SKImage.FromEncodedData(data)
                ?? throw new InvalidOperationException($"Could not decode image '{path}'.");
 
            _staticContent[nodeId] = image;
            return image;
        }
 
        private SKImage GetOrRasterizeText(TextInputNode text, int canvasWidth, int canvasHeight)
        {
            if (_staticContent.TryGetValue(text.Id, out SKImage? cached))
                return cached;
 
            string path = TextRasterizer.Rasterize(text, canvasWidth, canvasHeight, out _, out _);
            _ownedTempFiles[text.Id] = path;
 
            SKImage image = GetOrLoadStaticImage(text.Id, path);
            return image;
        }
 
        private NestedTimelineRenderer GetOrCreateNestedRenderer(TimelineVideoInputNode embed)
        {
            if (_nestedRenderers.TryGetValue(embed.Id, out NestedTimelineRenderer? existing))
                return existing;
 
            var renderer = new NestedTimelineRenderer(embed.Reference, _fps, _hwAccel);
            _nestedRenderers[embed.Id] = renderer;
            return renderer;
        }
 
        /// <summary>
        /// Terminates and forgets every Video-type MediaSourceNode decoder
        /// belonging to `clip`, once its visible window is over (see
        /// RenderContentPreparation's decoder-release schedule). A no-op for
        /// any clip that never had one.
        /// </summary>
        public void ReleaseDecoder(Clip clip)
        {
            if (!_videoDecodersByClip.Remove(clip, out List<Guid>? nodeIds)) return;
 
            foreach (Guid nodeId in nodeIds)
            {
                if (_videoDecoders.Remove(nodeId, out SkSourceDecoder? decoder))
                    decoder.Dispose();
            }
        }
 
        public void Dispose()
        {
            foreach (SkSourceDecoder decoder in _videoDecoders.Values) decoder.Dispose();
            _videoDecoders.Clear();
            _videoDecodersByClip.Clear();
 
            foreach (SKImage image in _staticContent.Values) image.Dispose();
            _staticContent.Clear();
 
            foreach (NestedTimelineRenderer renderer in _nestedRenderers.Values) renderer.Dispose();
            _nestedRenderers.Clear();
 
            foreach (string path in _ownedTempFiles.Values)
            {
                try { System.IO.File.Delete(path); } catch { /* best-effort cleanup */ }
            }
            _ownedTempFiles.Clear();
        }
    }
}
 