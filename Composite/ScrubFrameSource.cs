using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using EditSharp.Components;
using EditSharp.Components.Clips;
using EditSharp.Components.Nodes;
using EditSharp.Components.Nodes.Sources.Video;

namespace EditSharp.Composite
{
    /// <summary>
    /// Resolves one frame's content for an ARBITRARY, non-sequential
    /// timeline position — the engine behind Playback.ScrubToAsync and
    /// reverse playback (see Playback's class remarks, "I-FRAME-ONLY
    /// SCRUB/REVERSE").
    ///
    /// DELIBERATELY NOT SkClipContentSource: that class's video path
    /// (SkSourceDecoder.Start/NextFrame) is a persistent, forward-only pipe
    /// — exactly wrong for "jump to an arbitrary position right now," which
    /// is the whole point here. Instead, every Video-type VideoSourceNode is
    /// decoded with a fresh ONE-SHOT ffmpeg process seeked directly to the
    /// nearest preceding I-frame (KeyframeIndex + SkSourceDecoder.
    /// DecodeSingleFrameAsync) — no persistent decoder, no forward-stepping
    /// buffer, genuinely O(1) regardless of how far the jump is. The
    /// trade-off, named not hidden: the delivered frame is only ACCURATE TO
    /// THE NEAREST KEYFRAME, not the exact requested frame — expected for a
    /// scrub/rewind preview, wrong for a final render (this class is never
    /// used by Render/*).
    ///
    /// A small per-node cache remembers the LAST keyframe decoded (its own
    /// seek timestamp + the resulting image) so repeated calls landing in
    /// the same GOP — scrubbing back and forth by a few frames, or reverse
    /// playback ticking faster than the keyframe interval — reuse it
    /// instead of re-spawning ffmpeg.
    ///
    /// CANCELLATION (`ct` on PrefetchAsync): threaded through to
    /// KeyframeIndex.GetAsync (via WaitAsync — see that call site's own
    /// remarks on why the shared keyframe scan itself is NOT cancelled,
    /// only this caller's wait on it) and to SkSourceDecoder.
    /// DecodeSingleFrameAsync (which DOES fully cancel/kill its own
    /// one-shot ffmpeg process — see that method's own remarks). This is
    /// what lets Playback.ScrubToAsync's "latest request wins" coalescing
    /// actually stop in-flight work promptly instead of only stopping
    /// queued-but-not-yet-started work.
    ///
    /// PREFETCH / GetContent SPLIT, DELIBERATE: IClipContentSource.GetContent
    /// must be synchronous (SkFrameCompositor calls it mid-composite, with
    /// no async path down that call stack), but the real work here — the
    /// keyframe lookup and decode — is genuinely async. Rather than block on
    /// the async work from inside a sync call (a real deadlock risk in a
    /// host app with a synchronization context), every visible clip for the
    /// frame being composed is resolved ahead of time via PrefetchAsync, and
    /// GetContent just serves what was already resolved. Callers (Playback)
    /// are expected to PrefetchAsync every VideoClip a FrameState says is
    /// visible before handing this source to SkFrameCompositor.
    ///
    /// STATIC CONTENT (images, rasterized text) and GENERATOR/NOISE nodes
    /// are already random-access by construction — dispatches the same way
    /// SkClipContentSource does for those; no shared base class since video
    /// handling is otherwise completely different between the two.
    /// </summary>
    internal sealed class ScrubFrameSource : IClipContentSource, IDisposable
    {
        private readonly int _fps;
        private readonly HardwareAccelerator _hwAccel;
        private readonly IReadOnlyDictionary<Guid, (int Width, int Height)> _nativeSizes;
        private readonly IReadOnlyDictionary<Guid, DecodeHwAccelPlan> _decodePlans;

        private readonly Dictionary<Guid, SKImage> _staticContent = new();
        private readonly Dictionary<Guid, string> _ownedTempFiles = new();
        private readonly Dictionary<Guid, NestedTimelineRenderer> _nestedRenderers = new();

        // Per Video-type VideoSourceNode: the last keyframe timestamp
        // decoded and its image — kept alive across calls (owned by this
        // class, never disposed by a caller) so repeated scrub/reverse ticks
        // landing in the same GOP reuse it. See class remarks.
        private readonly Dictionary<Guid, (double SeekSeconds, SKImage Image)> _lastKeyframe = new();

        // Resolved content for the frame currently being composed —
        // populated by PrefetchAsync (does the real async decode work),
        // consumed by GetContent (sync, satisfies IClipContentSource). See
        // class remarks on why this split exists.
        private readonly Dictionary<Clip, IReadOnlyDictionary<Guid, (SKImage Image, bool Transient)>> _prefetched = new();

        public ScrubFrameSource(
            int fps,
            HardwareAccelerator hwAccel,
            IReadOnlyDictionary<Guid, (int Width, int Height)> nativeSizes,
            IReadOnlyDictionary<Guid, DecodeHwAccelPlan> decodePlans)
        {
            _fps = fps;
            _hwAccel = hwAccel;
            _nativeSizes = nativeSizes;
            _decodePlans = decodePlans;
        }

        /// <summary>
        /// Resolves and caches every InputNode's content for `clip` at
        /// arbitrary clip-relative time `clipSeconds`, ready for a following
        /// GetContent call for the same clip. Must be called for every
        /// VideoClip a FrameState reports visible, before that FrameState is
        /// handed to SkFrameCompositor.
        /// </summary>
        public async Task PrefetchAsync(
            VideoClip clip, double clipSeconds, int canvasWidth, int canvasHeight, SkSurfacePool pool,
            CancellationToken ct = default)
        {
            var result = new Dictionary<Guid, (SKImage, bool)>();

            foreach (InputNode node in clip.Graph.Nodes.OfType<InputNode>())
            {
                result[node.Id] = node switch
                {
                    VideoSourceNode { Source.Type: SourceType.Video } media =>
                        (await GetKeyframeAsync(clip, media, clipSeconds, canvasWidth, canvasHeight, ct), false),

                    VideoSourceNode { Source.Type: SourceType.Image } media =>
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
                        $"ScrubFrameSource has no dispatch for {node.GetType().Name}."),
                };
            }

            _prefetched[clip] = result;
        }

        public IReadOnlyDictionary<Guid, (SKImage Image, bool Transient)> GetContent(
            VideoClip clip, double clipSeconds, int canvasWidth, int canvasHeight, SkSurfacePool pool)
        {
            if (!_prefetched.TryGetValue(clip, out var content))
                throw new InvalidOperationException(
                    "ScrubFrameSource.GetContent called without a matching PrefetchAsync call for this " +
                    "clip/frame — every visible VideoClip must be prefetched before this source is handed " +
                    "to SkFrameCompositor (see Playback's ComposeInstantFrameAsync).");

            return content;
        }

        /// <summary>
        /// Clears this frame's resolved-content lookup (NOT the persistent
        /// keyframe/static/nested caches — see class remarks). Call once a
        /// frame has been fully composited so a clip that's no longer
        /// visible next tick can't be looked up by accident.
        /// </summary>
        public void ClearPrefetch() => _prefetched.Clear();

        private async Task<SKImage> GetKeyframeAsync(
            VideoClip clip, VideoSourceNode media, double clipSeconds, int canvasWidth, int canvasHeight,
            CancellationToken ct)
        {
            double targetSeconds = (media.Source.Start ?? TimeSpan.Zero).TotalSeconds + Math.Max(0, clipSeconds);

            // .WaitAsync(ct) makes THIS caller's wait cancellable without
            // cancelling the underlying scan itself — KeyframeIndex.GetAsync
            // is a shared, cached Task per source path, and a source's first
            // scan can take real time on a long file; if a superseded scrub
            // request cancelled the scan outright, the NEXT request would
            // have to restart it from scratch, and under a fast scrub drag
            // that scan could keep getting killed and restarted forever
            // without ever finishing — see Playback's own remarks on this
            // exact failure mode.
            IReadOnlyList<double> keyframes = await KeyframeIndex.GetAsync(media.Source.Path).WaitAsync(ct);
            double seekSeconds = KeyframeIndex.FindAtOrBefore(keyframes, targetSeconds);

            if (_lastKeyframe.TryGetValue(media.Id, out var cached) && cached.SeekSeconds == seekSeconds)
                return cached.Image;

            (int nativeWidth, int nativeHeight) = _nativeSizes.TryGetValue(media.Id, out var size)
                ? size
                : (0, 0);

            if (nativeWidth <= 0 || nativeHeight <= 0)
                throw new InvalidOperationException(
                    "No native size registered for a VideoSourceNode — the caller must probe native sizes " +
                    "before scrubbing/reverse playback (see Playback.PrepareScrubNativeInfoAsync).");

            ClipTransform transform =
                GraphSearchHelpers.FindDownstreamTransform(clip.Graph, media)?.Transform ?? new ClipTransform();

            (int desiredWidth, int desiredHeight) = TransformExpressions.ComputeContentSize(
                transform, nativeWidth, nativeHeight, canvasWidth, canvasHeight);

            int decodeWidth = Math.Min(desiredWidth, nativeWidth);
            int decodeHeight = Math.Min(desiredHeight, nativeHeight);

            DecodeHwAccelPlan plan = _decodePlans.TryGetValue(media.Id, out DecodeHwAccelPlan? resolvedPlan)
                ? resolvedPlan
                : DecodeHwAccelPlan.Software;

            SKImage image = await SkSourceDecoder.DecodeSingleFrameAsync(
                media.Source.Path, seekSeconds, decodeWidth, decodeHeight, plan, ct);

            if (_lastKeyframe.TryGetValue(media.Id, out var previous))
                previous.Image.Dispose();

            _lastKeyframe[media.Id] = (seekSeconds, image);
            return image;
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

            return GetOrLoadStaticImage(text.Id, path);
        }

        private NestedTimelineRenderer GetOrCreateNestedRenderer(TimelineVideoInputNode embed)
        {
            if (_nestedRenderers.TryGetValue(embed.Id, out NestedTimelineRenderer? existing))
                return existing;

            var renderer = new NestedTimelineRenderer(embed.Reference, _fps, _hwAccel);
            _nestedRenderers[embed.Id] = renderer;
            return renderer;
        }

        public void Dispose()
        {
            _prefetched.Clear();

            foreach ((double _, SKImage image) in _lastKeyframe.Values) image.Dispose();
            _lastKeyframe.Clear();

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