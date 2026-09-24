using System;
using EditSharp.Components;
using EditSharp.Components.Clips;
using EditSharp.Compositing.Gpu;
using SkiaSharp;

namespace EditSharp.Compositing.Sources
{
    /// <summary>
    /// Recursively renders one frame of an embedded Timeline, for a
    /// TimelineVideoInputNode. One instance per node, cached by its parent
    /// ClipContentSource for the node's whole visible window.
    ///
    /// The nested timeline reads its sources on demand with its parent's
    /// options (read mode, source mode, failure policy, report); unbuffered,
    /// since its frames come one at a time from inside the parent's composite.
    ///
    /// KNOWN GAP, flagged rather than hidden: this does NOT share the parent
    /// render's readers, GPU context or surface pool; a nested timeline gets
    /// its OWN full set, recursively. Correct output, not the most
    /// resource-efficient possible implementation.
    /// </summary>
    internal sealed class NestedTimelineRenderer : IDisposable
    {
        private readonly Timeline _timeline;
        private readonly TimelineReference _reference;
        private readonly ContentSourceOptions _options;

        private GpuContext? _gpuContext;
        private SurfacePool? _pool;
        private ClipContentSource? _contentSource;

        public NestedTimelineRenderer(TimelineReference reference, ContentSourceOptions options)
        {
            _reference = reference;
            _timeline = reference.Timeline;
            _options = options;
        }

        public SKImage RenderFrame(double clipSeconds, int canvasWidth, int canvasHeight)
        {
            if (_contentSource is null)
            {
                _gpuContext = GpuContext.Create(_options.HwAccel);
                _pool = new SurfacePool(_gpuContext.GRContext, canvasWidth, canvasHeight, _timeline.VideoChannels.Count);
                _contentSource = new ClipContentSource(_options with { CanvasWidth = canvasWidth, CanvasHeight = canvasHeight });
            }

            int fps = _options.Fps;
            TimeSpan refStart = _reference.Start ?? TimeSpan.Zero;
            double nestedSeconds = refStart.TotalSeconds + Math.Max(0.0, clipSeconds);
            double maxSeconds = Math.Max(0.0, _timeline.Duration.TotalSeconds - (1.0 / fps));
            nestedSeconds = Math.Clamp(nestedSeconds, 0.0, maxSeconds);

            int frameIndex = (int)Math.Round(nestedSeconds * fps);
            FrameState state = FrameStateResolver.Resolve(_timeline, frameIndex, fps);

            return FrameCompositor.ComposeFrameImage(state, _contentSource, canvasWidth, canvasHeight, fps, _pool!);
        }

        public void Dispose()
        {
            _contentSource?.Dispose();
            _pool?.Dispose();
            _gpuContext?.Dispose();
        }
    }
}
