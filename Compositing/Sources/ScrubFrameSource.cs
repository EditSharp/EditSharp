using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using EditSharp.Components;
using EditSharp.Components.Clips;
using EditSharp.Components.Nodes;
using EditSharp.Components.Nodes.Sources;
using EditSharp.Caching.ScrubProxy;
using EditSharp.Compositing.Generators;
using EditSharp.Compositing.Gpu;
using EditSharp.Video;

namespace EditSharp.Compositing.Sources
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
    /// caller likes. DELIBERATELY NOT ClipContentSource: that class's
    /// video path (SourceDecoder.Start/NextFrame) is a persistent,
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
    /// PROXY RESOLUTION IS THE CALLER'S JOB, BUT NO LONGER A GUARANTEE THIS
    /// CLASS CAN LEAN ON (changed here, paired with Playback's own SCRUB
    /// PROXY BUILDS RUN ON A REAL BACKGROUND THREAD fix): `proxies` (keyed
    /// by VideoSourceNode Id) is handed in, and Playback kicks off every
    /// referenced source's resolution via KickOffScrubProxyResolution — but
    /// that now runs on a background thread and is explicitly NOT awaited
    /// as part of session setup, so at the moment this class is asked for
    /// a frame, `proxies` may well have NO entry yet for a node whose build
    /// simply hasn't finished. That is now a completely ordinary, expected
    /// state, not a caller bug — see MEDIA-OFFLINE PLACEHOLDER below for
    /// what this class does about it.
    ///
    /// MEDIA-OFFLINE PLACEHOLDER FOR ANYTHING NOT YET (OR NEVER GOING TO
    /// BE) RESOLVABLE (new here, direct response to real-world testing —
    /// user request: "the placeholder should just be used anytime any
    /// media comes up empty"): every one of this class's own content
    /// dispatches — the scrub-proxy video path, static-image loading, text
    /// rasterization — is now wrapped so that ANY failure to produce real
    /// content (no proxy entry yet, a proxy whose build failed outright, a
    /// missing/corrupt image file, a rasterization failure) falls back to
    /// MediaPlaceholder.Get(canvasWidth, canvasHeight) instead of throwing
    /// and breaking the whole frame's composite. A node that fails once is
    /// remembered in `_knownBroken` so a broken STATIC node (image/text —
    /// nothing about a bad file or a rasterization failure fixes itself
    /// between ticks) doesn't keep retrying the same failing work every
    /// single frame; a video node is deliberately NOT remembered this way,
    /// since its proxy may still be building in the background and
    /// `_proxies` genuinely gains an entry once that finishes (see
    /// Playback's own ScrubProxyReady event) — every call simply re-checks
    /// `_proxies` fresh. `_knownBroken` is kept as its OWN HashSet,
    /// deliberately separate from `_staticContent`, so the shared,
    /// never-disposed MediaPlaceholder image is never itself stored in a
    /// dictionary that Dispose() below disposes wholesale.
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
    /// synchronous — FrameCompositor calls it mid-composite with no async
    /// path down that call stack — and every dispatch here already is
    /// synchronous). Kept because TimelineVideoInputNode's nested-renderer
    /// path and the overall PrefetchAsync/GetContent contract are shared
    /// with ClipContentSource's interface shape; splitting it out again
    /// if/when nothing async-shaped remains at all is a reasonable future
    /// simplification, not done here to keep this change focused.
    ///
    /// STATIC CONTENT (images, rasterized text) and GENERATOR/NOISE nodes
    /// are already random-access by construction — dispatches the same way
    /// ClipContentSource does for those; no shared base class since video
    /// handling is otherwise completely different between the two.
    ///
    /// EVERY VIDEO-PROXY FRAME IS READ ON THE CPU — there is no GPU decode
    /// path for a scrub proxy frame in this codebase; an earlier round of
    /// GPU decode work for IndexedDelta7 was tried and then removed
    /// entirely as unneeded complexity (decided in conversation — the GPU
    /// work that actually matters here is on the BUILD/encode side, see
    /// ScrubProxyGpuEncoder, not the read side, since a proxy read is
    /// already a cheap, single positioned file read regardless of pixel
    /// format). GetProxyFrame below is therefore just
    /// `reader.GetFrameAt(...)`, unconditionally, for every pixel format.
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

        // See class remarks, MEDIA-OFFLINE PLACEHOLDER — nodes whose
        // static content (image/text) has already failed once, so repeat
        // ticks don't keep re-attempting the same doomed work. Video nodes
        // are deliberately NOT tracked here — see remarks.
        private readonly HashSet<Guid> _knownBroken = new();

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
        /// handed to FrameCompositor.
        ///
        /// Fully synchronous under the hood now — see class remarks on
        /// CANCELLATION — but keeps a Task-returning signature for call-site
        /// stability with Playback.ComposeInstantFrameAsync.
        /// </summary>
        public Task PrefetchAsync(
            VideoClip clip, double clipSeconds, int canvasWidth, int canvasHeight, SurfacePool pool,
            CancellationToken ct = default)
        {
            var result = new Dictionary<Guid, (SKImage, bool)>();

            foreach (InputNode node in clip.Graph.Nodes.OfType<InputNode>())
            {
                result[node.Id] = node switch
                {
                    VideoSourceNode { Source.Type: SourceType.Video } media =>
                        (GetProxyFrame(media, clipSeconds, canvasWidth, canvasHeight), false),

                    VideoSourceNode { Source.Type: SourceType.Image } media =>
                        (GetOrLoadStaticImage(media.Id, media.Source.Path, canvasWidth, canvasHeight), false),

                    TextInputNode text =>
                        (GetOrRasterizeText(text, canvasWidth, canvasHeight), false),

                    ColorGeneratorInputNode color =>
                        (ColorGenerator.Render(color, clipSeconds, canvasWidth, canvasHeight, pool), true),

                    NoiseInputNode noise =>
                        (NoiseGenerator.Render(noise, clipSeconds, canvasWidth, canvasHeight, pool), true),

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
            VideoClip clip, double clipSeconds, int canvasWidth, int canvasHeight, SurfacePool pool)
        {
            if (!_prefetched.TryGetValue(clip, out var content))
                throw new InvalidOperationException(
                    "ScrubFrameSource.GetContent called without a matching PrefetchAsync call for this " +
                    "clip/frame — every visible VideoClip must be prefetched before this source is handed " +
                    "to FrameCompositor (see Playback's ComposeInstantFrameAsync).");

            return content;
        }

        /// <summary>
        /// Clears this frame's resolved-content lookup (NOT the persistent
        /// proxy-reader/static/nested caches — see class remarks). Call once
        /// a frame has been fully composited so a clip that's no longer
        /// visible next tick can't be looked up by accident.
        /// </summary>
        public void ClearPrefetch() => _prefetched.Clear();

        /// <summary>
        /// See class remarks, MEDIA-OFFLINE PLACEHOLDER: any failure here
        /// — most commonly `_proxies` simply not having an entry for this
        /// node yet (its build hasn't finished, or failed) — falls back to
        /// the shared offline placeholder rather than throwing and
        /// breaking the whole frame's composite. Deliberately re-checks
        /// `_proxies` fresh every call rather than caching a "this node is
        /// broken" verdict — a still-building proxy legitimately becomes
        /// available partway through a scrub/reverse session (see
        /// Playback's ScrubProxyReady event). See class remarks, EVERY
        /// VIDEO-PROXY FRAME IS READ ON THE CPU — no GPU decode attempt
        /// happens here for any pixel format.
        /// </summary>
        private SKImage GetProxyFrame(VideoSourceNode media, double clipSeconds, int canvasWidth, int canvasHeight)
        {
            try
            {
                ScrubProxyReader? reader = TryGetOrOpenReader(media);
                if (reader == null) return MediaPlaceholder.Get(canvasWidth, canvasHeight);

                double targetSeconds = (media.Source.Start ?? TimeSpan.Zero).TotalSeconds + Math.Max(0, clipSeconds);

                return reader.GetFrameAt(targetSeconds);
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogWarning(
                    $"ScrubFrameSource: falling back to the offline placeholder for '{media.Source.Path}': " +
                    $"{ex.Message}");
                return MediaPlaceholder.Get(canvasWidth, canvasHeight);
            }
        }

        /// <summary>
        /// Returns this node's already-open (or freshly-opened) proxy
        /// reader, or null if `_proxies` has no entry for it yet — no
        /// longer treated as a caller bug (see class remarks): a
        /// background-kicked-off proxy build simply may not have finished,
        /// or may have failed outright, by the time a frame is requested.
        /// </summary>
        private ScrubProxyReader? TryGetOrOpenReader(VideoSourceNode media)
        {
            if (_proxyReaders.TryGetValue(media.Id, out ScrubProxyReader? existing))
                return existing;

            if (!_proxies.TryGetValue(media.Id, out ScrubProxyEntry entry))
                return null;

            ScrubProxyReader reader = ScrubProxyReader.Open(entry.Path);
            _proxyReaders[media.Id] = reader;
            return reader;
        }

        /// <summary>
        /// See class remarks, MEDIA-OFFLINE PLACEHOLDER: a missing/corrupt
        /// image falls back to the offline placeholder rather than
        /// throwing. `nodeId` is remembered in `_knownBroken` on failure so
        /// a bad file isn't re-read every single tick — nothing about a
        /// decode failure here is expected to resolve itself mid-session,
        /// unlike a still-building video proxy.
        /// </summary>
        private SKImage GetOrLoadStaticImage(Guid nodeId, string path, int canvasWidth, int canvasHeight)
        {
            if (_staticContent.TryGetValue(nodeId, out SKImage? cached))
                return cached;

            if (_knownBroken.Contains(nodeId))
                return MediaPlaceholder.Get(canvasWidth, canvasHeight);

            try
            {
                using SKData data = SKData.Create(path)
                    ?? throw new InvalidOperationException($"Could not read '{path}'.");

                SKImage image = SKImage.FromEncodedData(data)
                    ?? throw new InvalidOperationException($"Could not decode image '{path}'.");

                _staticContent[nodeId] = image;
                return image;
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogWarning(
                    $"ScrubFrameSource: falling back to the offline placeholder for '{path}': {ex.Message}");
                _knownBroken.Add(nodeId);
                return MediaPlaceholder.Get(canvasWidth, canvasHeight);
            }
        }

        /// <summary>
        /// See class remarks, MEDIA-OFFLINE PLACEHOLDER: a rasterization
        /// failure falls back to the offline placeholder, and `text.Id` is
        /// remembered in `_knownBroken` the same way a broken static image
        /// is — same reasoning, nothing about a rasterization failure is
        /// expected to fix itself mid-session.
        /// </summary>
        private SKImage GetOrRasterizeText(TextInputNode text, int canvasWidth, int canvasHeight)
        {
            if (_staticContent.TryGetValue(text.Id, out SKImage? cached))
                return cached;

            if (_knownBroken.Contains(text.Id))
                return MediaPlaceholder.Get(canvasWidth, canvasHeight);

            try
            {
                string path = TextRasterizer.Rasterize(text, canvasWidth, canvasHeight, out _, out _);
                _ownedTempFiles[text.Id] = path;

                return GetOrLoadStaticImage(text.Id, path, canvasWidth, canvasHeight);
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogWarning(
                    $"ScrubFrameSource: falling back to the offline placeholder for a text node: {ex.Message}");
                _knownBroken.Add(text.Id);
                return MediaPlaceholder.Get(canvasWidth, canvasHeight);
            }
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

            // NOTE: _staticContent never holds the shared MediaPlaceholder
            // image (see class remarks, MEDIA-OFFLINE PLACEHOLDER) — every
            // image disposed here was actually loaded/rasterized by this
            // instance, so disposing them all is safe.
            foreach (SKImage image in _staticContent.Values) image.Dispose();
            _staticContent.Clear();

            _knownBroken.Clear();

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