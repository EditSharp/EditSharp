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
    /// reverse playback (see Playback's class remarks).
    ///
    /// REWRITTEN TO USE ScrubProxyReader, NOT ffmpeg, AT ALL (decided in
    /// conversation, after two rounds of real-world testing showed ANY
    /// per-tick ffmpeg process — GPU or software — could not be made both
    /// fast and crash-safe under a fast scrub drag): every Video-type
    /// VideoSourceNode's frame now comes from a small, PRE-BUILT, raw,
    /// decoder-less scrub proxy (see ScrubProxyFormat/ScrubProxyReader) —
    /// a direct positioned file read, no subprocess, no decode, no GOP/
    /// keyframe concept, genuinely O(1) and safe to call as fast as a
    /// caller likes. DELIBERATELY NOT SkClipContentSource: that class's
    /// video path (SkSourceDecoder.Start/NextFrame) is a persistent,
    /// forward-only pipe — exactly wrong for "jump to an arbitrary
    /// position right now." The trade-off, named not hidden: what's
    /// delivered is accurate to the proxy's own fixed SAMPLE RATE and low
    /// fixed RESOLUTION (EditSharpConfig.ScrubProxySampleRate/
    /// ScrubProxyTargetShortSide), not the exact requested frame or the
    /// clip's real decode resolution — expected for a scrub/rewind
    /// preview, wrong for a final render (this class is never used by
    /// Render/*).
    ///
    /// NO PER-NODE "LAST FRAME" CACHE ANY MORE — unlike the previous ffmpeg-
    /// backed version, a proxy read is already so cheap (one positioned
    /// read of a few hundred KB, no process) that caching the result across
    /// calls buys nothing worth the complexity; every call just reads fresh.
    ///
    /// PROXY RESOLUTION IS THE CALLER'S JOB, NOT THIS CLASS'S: `proxies`
    /// (keyed by VideoSourceNode Id) is handed in fully resolved — Playback
    /// builds/looks up every referenced source's scrub proxy (via
    /// ScrubProxyCache, blocking on any missing build) as part of scrub/
    /// reverse SESSION setup, once, before this class is ever asked for a
    /// frame — see Playback.PrepareScrubProxiesAsync. A VideoSourceNode
    /// with no entry here is a caller bug, not a runtime fallback case (see
    /// GetOrOpenReader).
    ///
    /// ScrubProxyReader INSTANCES ARE OWNED AND CACHED HERE, ONE PER SOURCE,
    /// OPENED LAZILY ON FIRST USE, AND DISPOSED WITH THIS CLASS — opening a
    /// proxy file is itself cheap (one small header read) but there's no
    /// reason to pay it more than once per source per scrub/reverse session.
    ///
    /// CANCELLATION (`ct` on PrefetchAsync): kept on the signature for
    /// interface stability with Playback.ComposeInstantFrameAsync and in
    /// case a future InputNode type needs real async work, but nothing in
    /// this class's own dispatch does any I/O wait worth cancelling any
    /// more — a ScrubProxyReader read is a single fast positioned read, not
    /// a subprocess. PrefetchAsync is therefore fully synchronous under the
    /// hood now (see its own remarks) despite the async-looking signature.
    ///
    /// PREFETCH / GetContent SPLIT, KEPT FOR NOW even though the video path
    /// no longer strictly needs it (IClipContentSource.GetContent must be
    /// synchronous — SkFrameCompositor calls it mid-composite with no async
    /// path down that call stack — and every dispatch here already is
    /// synchronous). Kept because TimelineVideoInputNode's nested-renderer
    /// path and the overall PrefetchAsync/GetContent contract are shared
    /// with SkClipContentSource's interface shape; splitting it out again
    /// if/when nothing async-shaped remains at all is a reasonable future
    /// simplification, not done here to keep this change focused.
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
        private readonly IReadOnlyDictionary<Guid, ScrubProxyEntry> _proxies;

        private readonly Dictionary<Guid, ScrubProxyReader> _proxyReaders = new();
        private readonly Dictionary<Guid, SKImage> _staticContent = new();
        private readonly Dictionary<Guid, string> _ownedTempFiles = new();
        private readonly Dictionary<Guid, NestedTimelineRenderer> _nestedRenderers = new();

        // Resolved content for the frame currently being composed —
        // populated by PrefetchAsync, consumed by GetContent (sync,
        // satisfies IClipContentSource). See class remarks on why this
        // split is kept even though PrefetchAsync is fully synchronous now.
        private readonly Dictionary<Clip, IReadOnlyDictionary<Guid, (SKImage Image, bool Transient)>> _prefetched = new();

        public ScrubFrameSource(
            int fps,
            HardwareAccelerator hwAccel,
            IReadOnlyDictionary<Guid, ScrubProxyEntry> proxies)
        {
            _fps = fps;
            _hwAccel = hwAccel;
            _proxies = proxies;
        }

        /// <summary>
        /// Resolves and caches every InputNode's content for `clip` at
        /// arbitrary clip-relative time `clipSeconds`, ready for a following
        /// GetContent call for the same clip. Must be called for every
        /// VideoClip a FrameState reports visible, before that FrameState is
        /// handed to SkFrameCompositor.
        ///
        /// Fully synchronous under the hood now — see class remarks on
        /// CANCELLATION — but keeps a Task-returning signature for call-site
        /// stability with Playback.ComposeInstantFrameAsync.
        /// </summary>
        public Task PrefetchAsync(
            VideoClip clip, double clipSeconds, int canvasWidth, int canvasHeight, SkSurfacePool pool,
            CancellationToken ct = default)
        {
            var result = new Dictionary<Guid, (SKImage, bool)>();

            foreach (InputNode node in clip.Graph.Nodes.OfType<InputNode>())
            {
                result[node.Id] = node switch
                {
                    VideoSourceNode { Source.Type: SourceType.Video } media =>
                        (GetProxyFrame(media, clipSeconds), false),

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
            return Task.CompletedTask;
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
        /// proxy-reader/static/nested caches — see class remarks). Call once
        /// a frame has been fully composited so a clip that's no longer
        /// visible next tick can't be looked up by accident.
        /// </summary>
        public void ClearPrefetch() => _prefetched.Clear();

        private SKImage GetProxyFrame(VideoSourceNode media, double clipSeconds)
        {
            double targetSeconds = (media.Source.Start ?? TimeSpan.Zero).TotalSeconds + Math.Max(0, clipSeconds);
            return GetOrOpenReader(media).GetFrameAt(targetSeconds);
        }

        private ScrubProxyReader GetOrOpenReader(VideoSourceNode media)
        {
            if (_proxyReaders.TryGetValue(media.Id, out ScrubProxyReader? existing))
                return existing;

            if (!_proxies.TryGetValue(media.Id, out ScrubProxyEntry entry))
                throw new InvalidOperationException(
                    "No scrub proxy registered for a VideoSourceNode — the caller must resolve/build every " +
                    "referenced source's scrub proxy before scrubbing/reverse playback (see " +
                    "Playback.PrepareScrubProxiesAsync).");

            ScrubProxyReader reader = ScrubProxyReader.Open(entry.Path);
            _proxyReaders[media.Id] = reader;
            return reader;
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

            foreach (ScrubProxyReader reader in _proxyReaders.Values) reader.Dispose();
            _proxyReaders.Clear();

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