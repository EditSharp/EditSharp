using System;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using EditSharp.Compositing;
using EditSharp.Compositing.Sources;
using EditSharp.Editing;
using EditSharp.History;

namespace EditSharp.Components.Sources.Video;

/// <summary>
/// Another timeline's picture, composited frame by frame. Start/Duration/Loop
/// trim it like any source; its natural length is the timeline's duration.
/// Saved as the timeline's Id (see SourceSerializer.Deserialize).
///
/// Compositor-bound: frames are composited with the parent's own GPU
/// context and surface pool, on its GPU thread, so they never cross GPU
/// contexts and are never prefetched.
/// </summary>
[SourceKind("timeline-video", DisplayName = "Timeline")]
public class TimelineVideoSource : VideoSource
{
    Timeline? _timeline;
    [Editable("Timeline")]
    public Timeline? Timeline { get => _timeline; set => Transaction.Set(this, ref _timeline, value, static (o, v) => o._timeline = v); }

    public override TimelineVideoSource Duplicate() => (TimelineVideoSource)base.Duplicate();

    public override Task<TimeSpan?> GetNaturalLengthAsync(CancellationToken ct = default) => Task.FromResult(Timeline?.Duration);

    internal override Task<IPreparedVideoSource> PrepareAsync(VideoPrepareContext context, CancellationToken ct = default) =>
        Timeline is null
            ? Task.FromException<IPreparedVideoSource>(new SourceUnavailableException(SourceUnavailableReason.MediaOffline, "No timeline is chosen."))
            : Task.FromResult<IPreparedVideoSource>(new Prepared(this));

    private protected override Timeline? ReferencedTimeline(Guid id) => Timeline?.Id == id ? Timeline : null;

    internal override void AddFingerprint(ref HashCode hash)
    {
        base.AddFingerprint(ref hash);
        hash.Add(Timeline?.Id);
    }

    private sealed class Prepared(TimelineVideoSource source) : IPreparedVideoSource, ICompositorBound
    {
        public (int Width, int Height) NativeSize => (0, 0);

        public IVideoFrameReader OpenReader(VideoReaderOptions options) => options.Compositor is { } compositor
            ? new Reader(source, options, compositor)
            : throw new SourceUnavailableException(SourceUnavailableReason.DecodeError, "A nested timeline can only be read while compositing.");

        public void Dispose() { }
    }

    /// <summary>
    /// Composites the nested timeline's frame at the mapped time with its own
    /// unbuffered content source (same read mode and failure policy as the
    /// parent) on the parent's surface pool.
    /// </summary>
    private sealed class Reader(TimelineVideoSource source, VideoReaderOptions options, CompositorAccess compositor) : IVideoFrameReader
    {
        private ClipContentSource? _content;

        public VideoFrame GetFrame(TimeSpan contentTime)
        {
            Timeline timeline = source.Timeline
                ?? throw new SourceUnavailableException(SourceUnavailableReason.MediaOffline, "No timeline is chosen.");

            TimeSpan time = source.MapTime(contentTime, timeline.Duration);
            SKSizeI canvas = GeneratedFrames.Canvas(options);
            int fps = compositor.Options.Fps;

            _content ??= new ClipContentSource(compositor.Options with
            {
                Buffered = false,
                Direction = 1,
                CanvasWidth = canvas.Width,
                CanvasHeight = canvas.Height,
            });

            int frame = (int)Math.Round(time.TotalSeconds * fps);
            FrameState state = FrameStateResolver.Resolve(timeline, frame, fps);
            return new VideoFrame(FrameCompositor.ComposeFrameImage(state, _content, canvas.Width, canvas.Height, fps, compositor.Pool), Transient: true);
        }

        public void Dispose() => _content?.Dispose();
    }
}
