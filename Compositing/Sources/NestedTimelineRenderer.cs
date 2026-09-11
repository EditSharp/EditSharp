using System;
using System.Collections.Concurrent;
using EditSharp.Components;
using EditSharp.Components.Clips;
using EditSharp.Compositing.Gpu;
using EditSharp.Video;
using SkiaSharp;

namespace EditSharp.Compositing.Sources
{
    /// <summary>
    /// Recursively renders one frame of an embedded Timeline, for a
    /// TimelineVideoInputNode. Replaces the old TimelineVideoClip Clip
    /// subtype's rendering path — embedding a nested Timeline is no longer a
    /// distinct kind of clip, just an ordinary InputNode inside a VideoClip's
    /// graph (see VideoEffectNodes.cs), and this is the piece that actually
    /// produces its pixel content.
    ///
    /// One instance per TimelineVideoInputNode, cached by ClipContentSource
    /// across the node's whole visible window (recursively holding its own
    /// GpuContext/SurfacePool/ClipContentSource/decoder state), rather
    /// than re-preparing content and re-opening decoders every frame.
    ///
    /// KNOWN GAP, flagged rather than hidden: this does NOT share the parent
    /// render's decoder cache, GPU context, or surface pool — a nested
    /// timeline gets its OWN full set, recursively. For a deeply nested or
    /// heavily-embedded timeline this means more concurrent decoders/GPU
    /// surfaces than a fully shared-resource design would use. Correct
    /// output, not the most resource-efficient possible implementation.
    ///
    /// REWRITE ("channels split by kind"): the SurfacePool seed below now
    /// uses _timeline.VideoChannels.Count specifically, instead of the old
    /// mixed _timeline.Channels.Count — only a VideoChannel's clips ever need
    /// a GPU-backed canvas surface here (see SurfacePool's own remarks:
    /// this is a warm-start heuristic, not a hard cap).
    /// </summary>
    internal sealed class NestedTimelineRenderer : IDisposable
    {
        private readonly Timeline _timeline;
        private readonly TimelineReference _reference;
        private readonly int _fps;
        private readonly HardwareAccelerator _hwAccel;

        private GpuContext? _gpuContext;
        private SurfacePool? _pool;
        private ClipContentSource? _contentSource;
        private bool _prepared;

        private readonly ConcurrentDictionary<Guid, (int, int)> _nativeSizes = new();
        private readonly ConcurrentDictionary<Guid, DecodeHwAccelPlan> _decodePlans = new();
        private readonly ConcurrentDictionary<Guid, string> _decodeSourcePaths = new();

        public NestedTimelineRenderer(TimelineReference reference, int fps, HardwareAccelerator hwAccel)
        {
            _reference = reference;
            _timeline = reference.Timeline;
            _fps = fps;
            _hwAccel = hwAccel;
        }

        /// <summary>
        /// Renders the nested Timeline's frame corresponding to `clipSeconds`
        /// into the embedding clip, honoring TimelineReference.Start (where
        /// in the nested timeline playback begins) and holding the nested
        /// timeline's last frame once `clipSeconds` runs past its own
        /// Duration — same freeze-frame convention every other content
        /// source in this pipeline uses past its own end.
        /// </summary>
        public SKImage RenderFrame(double clipSeconds, int canvasWidth, int canvasHeight)
        {
            EnsurePrepared(canvasWidth, canvasHeight);

            TimeSpan refStart = _reference.Start ?? TimeSpan.Zero;
            double nestedSeconds = refStart.TotalSeconds + Math.Max(0.0, clipSeconds);

            double maxSeconds = Math.Max(0.0, _timeline.Duration.TotalSeconds - (1.0 / _fps));
            nestedSeconds = Math.Clamp(nestedSeconds, 0.0, maxSeconds);

            int frameIndex = (int)Math.Round(nestedSeconds * _fps);

            FrameState state = FrameStateResolver.Resolve(_timeline, frameIndex, _fps);

            return FrameCompositor.ComposeFrameImage(
                state, _contentSource!, canvasWidth, canvasHeight, _fps, _pool!);
        }

        private void EnsurePrepared(int canvasWidth, int canvasHeight)
        {
            if (_prepared) return;

            //Synchronous wait deliberately: this is called from inside the
            //synchronous, sequential per-frame render loop (FrameCompositor
            //has no async path), and preparing a nested timeline's own media
            //happens once, lazily, on its first visible frame — same timing
            //as a top-level decoder's own lazy open.
            ContentPreparation
                .PrepareContentAsync(_timeline, canvasWidth, canvasHeight, _hwAccel, _nativeSizes, _decodePlans, _decodeSourcePaths)
                .GetAwaiter().GetResult();

            _gpuContext = GpuContext.Create(_hwAccel);
            _pool = new SurfacePool(_gpuContext.GRContext, canvasWidth, canvasHeight, _timeline.VideoChannels.Count);
            _contentSource = new ClipContentSource(_fps, _hwAccel, _nativeSizes, _decodePlans, decodeSourcePaths: _decodeSourcePaths);

            _prepared = true;
        }

        public void Dispose()
        {
            _contentSource?.Dispose();
            _pool?.Dispose();
            _gpuContext?.Dispose();
        }
    }
}