using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using EditSharp.Components;
using EditSharp.Components.Channels;
using EditSharp.Components.Clips;
using EditSharp.Components.Nodes;
using EditSharp.Components.Nodes.Sources;
using EditSharp.Components.Sources;
using EditSharp.Components.Sources.Video;
using EditSharp.Compositing.Generators;
using EditSharp.Compositing.Gpu;
using EditSharp.Compositing.Transforms;
using EditSharp.History;
using EditSharp.Rendering;
using EditSharp.Video;

namespace EditSharp.Compositing.Sources
{
    /// <summary>How a session treats a source that can't provide a frame.</summary>
    internal enum ContentFailurePolicy
    {
        /// <summary>Labeled placeholder; MediaOffline/DecodeError are retried after EditSharpConfig.SourceRetryInterval.</summary>
        Preview,

        /// <summary>Labeled placeholder, recorded in the session's RenderReport; never retried.</summary>
        Export,
    }

    /// <summary>
    /// Everything a content session needs to know up front. Buffered sessions
    /// (playback, export) prefetch each media input on its own thread in
    /// `Direction` and should be driven through Anticipate/WaitReady; unbuffered
    /// ones (scrubbing, thumbnails, nested timelines) read on demand.
    /// </summary>
    internal sealed record ContentSourceOptions(
        int Fps,
        int CanvasWidth,
        int CanvasHeight,
        HardwareAccelerator HwAccel,
        SourceMode SourceMode,
        VideoReadMode ReadMode,
        ContentFailurePolicy Failures,
        bool Buffered = false,
        int Direction = 1,
        RenderReportBuilder? Report = null);

    /// <summary>
    /// Resolves this frame's image for every InputNode of a VideoClip's graph.
    /// Media inputs (VideoSourceNode) go through their VideoSource; prepared,
    /// read and failed entirely on the source's terms, so this class never
    /// knows what kind of source it's reading. Generators, text and nested
    /// timelines are still resolved here until they become sources too.
    ///
    /// ONE CLASS FOR EVERY CONTEXT: playback, export, scrubbing, reverse and
    /// thumbnails differ only in their ContentSourceOptions; read mode,
    /// whether reads are buffered and in which direction, and what a failure
    /// turns into.
    ///
    /// AHEAD OF TIME (buffered sessions): Anticipate, called once per frame
    /// before the frame is composed, prepares the sources of every clip that
    /// will be on screen within EditSharpConfig.SourceLookahead, opens a
    /// BufferedVideoReader for each as soon as it's prepared (so its first
    /// frames are decoded before it appears), and releases inputs whose clip
    /// the playhead has left. WaitReady then lets the caller decide what to do
    /// about a frame that isn't ready in time (see Playback's late-frame
    /// handling).
    ///
    /// FAILURES: a source's SourceUnavailableException becomes a
    /// MediaPlaceholder for its reason (EndOfSource: transparent). Opening and
    /// ProxyPending also mark the frame incomplete (LastFrameIncomplete).
    /// MediaOffline and DecodeError are remembered per input; a Preview
    /// session tries again after EditSharpConfig.SourceRetryInterval, an
    /// Export session records every affected frame in its RenderReport.
    ///
    /// NOT THREAD-SAFE: Anticipate, WaitReady, PrepareAsync's caller and
    /// GetContent must be driven from one logical sequence (they are, by every
    /// session loop). Only preparation and each buffer's producer run in the
    /// background, and they touch nothing shared.
    /// </summary>
    internal sealed class ClipContentSource : IClipContentSource, IDisposable
    {
        private sealed class MediaInput(VideoClip clip, VideoSourceNode node, VideoSource source)
        {
            public VideoClip Clip { get; } = clip;
            public VideoSourceNode Node { get; } = node;
            public VideoSource Source { get; } = source;

            public Task<IPreparedVideoSource>? Preparing { get; set; }
            public IPreparedVideoSource? Prepared { get; set; }
            public IVideoFrameReader? Reader { get; set; }
            public BufferedVideoReader? Buffer { get; set; }

            public SourceUnavailableException? Failure { get; set; }
            public long FailedAt { get; set; }
        }

        private readonly ContentSourceOptions _options;

        private readonly Dictionary<Guid, MediaInput> _media = new();
        private readonly Dictionary<Guid, SKImage> _staticContent = new();
        private readonly Dictionary<Guid, string> _ownedTempFiles = new();
        private readonly Dictionary<Guid, NestedTimelineRenderer> _nestedRenderers = new();
        private readonly HashSet<Guid> _brokenStatic = new();

        public ClipContentSource(ContentSourceOptions options) => _options = options;

        /// <summary>Whether the last GetContent substituted anything still on its way (Opening, ProxyPending).</summary>
        public bool LastFrameIncomplete { get; private set; }

        // ---------------------------------------------------------------
        // Ahead of time
        // ---------------------------------------------------------------

        /// <summary>Prepares every media input visible in `state`, waiting for all of them. Failures are recorded, not thrown.</summary>
        public Task PrepareAsync(FrameState state, CancellationToken ct = default) => PrepareAsync(
            state.Channels.SelectMany(c => c.Clips).Where(c => c.Clip is VideoClip).Select(c => ((VideoClip)c.Clip, c.Graph)), ct);

        /// <summary>Prepares every media input of each clip, read from its graph snapshot. Failures are recorded, not thrown.</summary>
        public async Task PrepareAsync(IEnumerable<(VideoClip Clip, Graph Graph)> clips, CancellationToken ct = default)
        {
            var pending = new List<Task>();

            foreach ((VideoClip clip, Graph graph) in clips)
            {
                foreach (VideoSourceNode node in graph.Nodes.OfType<VideoSourceNode>())
                {
                    if (StartPreparing(Input(clip, node)) is { } task) pending.Add(task);
                }
            }

            try { await Task.WhenAll(pending).WaitAsync(ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* surfaced per input when read */ }
        }

        /// <summary>
        /// Buffered sessions, once per frame before composing it: get upcoming
        /// clips' sources prepared and buffering, and let go of passed ones.
        /// </summary>
        public void Anticipate(Timeline timeline, int frameIndex)
        {
            using var _ = ModelLock.Read();

            TimeSpan now = FrameStateResolver.TimeOfFrame(frameIndex, _options.Fps);
            TimeSpan lookahead = EditSharpConfig.SourceLookahead;
            var live = new HashSet<Guid>();

            foreach (VideoChannel channel in timeline.VideoChannels)
            {
                foreach (Clip clip in channel.Clips)
                {
                    if (clip is not VideoClip video) continue;

                    TimeSpan reachEnd = ReachEnd(channel, clip);

                    bool upcoming = _options.Direction > 0
                        ? clip.Start <= now + lookahead && reachEnd > now
                        : reachEnd >= now - lookahead && clip.Start <= now;

                    if (!upcoming) continue;

                    foreach (VideoSourceNode node in video.Graph.AllNodes.OfType<VideoSourceNode>())
                    {
                        MediaInput input = Input(video, node);
                        live.Add(node.Id);
                        StartPreparing(input);

                        if (_options.Buffered && input.Buffer is null && input.Failure is null && TryTakePrepared(input) is { } prepared)
                            OpenBuffer(input, prepared, video.Graph, EntryFrame(clip, reachEnd, frameIndex), reachEnd);
                    }
                }
            }

            foreach (MediaInput passed in _media.Values.Where(m => !live.Contains(m.Node.Id)).ToList())
            {
                Release(passed);
                _media.Remove(passed.Node.Id);
            }
        }

        /// <summary>
        /// Waits (up to `timeout`, or Timeout.InfiniteTimeSpan) until every
        /// media input of `state` can hand over its frame without blocking.
        /// Inputs that have failed count as ready; they'll show their
        /// placeholder. False means the frame isn't ready in time.
        /// </summary>
        public bool WaitReady(FrameState state, TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();
            TimeSpan Remaining() => timeout == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan : Max(TimeSpan.Zero, timeout - clock.Elapsed);

            foreach (FrameClip frameClip in state.Channels.SelectMany(c => c.Clips))
            {
                if (frameClip.Clip is not VideoClip clip) continue;

                foreach (VideoSourceNode node in frameClip.Graph.Nodes.OfType<VideoSourceNode>())
                {
                    MediaInput input = Input(clip, node);
                    if (input.Failure is not null) continue;

                    Task<IPreparedVideoSource>? preparing = StartPreparing(input);
                    if (preparing is not null && !WaitQuietly(preparing, Remaining())) return false;

                    if (!_options.Buffered) continue;

                    if (input.Buffer is null)
                    {
                        if (TryTakePrepared(input) is not { } prepared) continue; //failed: placeholder
                        OpenBuffer(input, prepared, frameClip.Graph, state.FrameIndex, ReachEnd(null, clip));
                    }

                    TimeSpan content = TimeSpan.FromSeconds(frameClip.ClipSeconds);
                    if (!input.Buffer!.WaitReady(state.FrameIndex, content, Remaining())) return false;
                }
            }

            return true;
        }

        // ---------------------------------------------------------------
        // Per frame
        // ---------------------------------------------------------------

        public IReadOnlyDictionary<Guid, (SKImage Image, bool Transient)> GetContent(
            VideoClip clip, Graph graph, double clipSeconds, int frameIndex, int canvasWidth, int canvasHeight, SurfacePool pool) =>
            GetContent(clip, graph, clipSeconds, frameIndex, canvasWidth, canvasHeight, pool, null);

        /// <summary>
        /// GetContent with some inputs already resolved (see
        /// GetMediaFramesOnceAsync): those are passed through untouched and
        /// only the rest are resolved here.
        /// </summary>
        public IReadOnlyDictionary<Guid, (SKImage Image, bool Transient)> GetContent(
            VideoClip clip, Graph graph, double clipSeconds, int frameIndex, int canvasWidth, int canvasHeight, SurfacePool pool,
            IReadOnlyDictionary<Guid, (SKImage Image, bool Transient)>? resolved)
        {
            var result = new Dictionary<Guid, (SKImage, bool)>();
            LastFrameIncomplete = false;

            foreach (InputNode node in graph.Nodes.OfType<InputNode>())
            {
                if (resolved is not null && resolved.TryGetValue(node.Id, out (SKImage Image, bool Transient) known))
                {
                    result[node.Id] = known;
                    continue;
                }

                result[node.Id] = node switch
                {
                    VideoSourceNode media => ResolveMedia(clip, graph, media, clipSeconds, frameIndex, canvasWidth, canvasHeight),

                    TextInputNode text => GetOrRasterizeText(text, frameIndex, canvasWidth, canvasHeight),

                    ColorGeneratorInputNode color =>
                        (ColorGenerator.Render(color, clipSeconds, canvasWidth, canvasHeight, pool), true),

                    NoiseInputNode noise =>
                        (NoiseGenerator.Render(noise, clipSeconds, canvasWidth, canvasHeight, pool), true),

                    TimelineVideoInputNode embed =>
                        (GetOrCreateNestedRenderer(embed).RenderFrame(clipSeconds, canvasWidth, canvasHeight), true),

                    _ => throw new NotSupportedException($"ClipContentSource has no dispatch for {node.GetType().Name}."),
                };
            }

            return result;
        }

        /// <summary>
        /// Every media input of `clip` at `clipSeconds` through a one-shot
        /// VideoSource.GetFrameAtAsync; nothing is kept open, so a caller
        /// touching many clips (thumbnails) holds no readers. Failures become
        /// placeholders; Complete is false if any was something still on its
        /// way (ProxyPending, Opening). Hand the result to GetContent.
        /// </summary>
        public async Task<(IReadOnlyDictionary<Guid, (SKImage Image, bool Transient)> Media, bool Complete)> GetMediaFramesOnceAsync(
            Graph graph, double clipSeconds, int width, int height, CancellationToken ct = default)
        {
            TimeSpan content = TimeSpan.FromSeconds(clipSeconds);
            VideoSourceNode[] nodes = graph.Nodes.OfType<VideoSourceNode>().ToArray();

            var frames = await Task.WhenAll(nodes.Select(async node =>
            {
                try
                {
                    SKImage image = await node.Source.GetFrameAtAsync(content, _options.SourceMode, width, height, ct);
                    return (Id: node.Id, Image: image, Transient: true, Pending: false);
                }
                catch (SourceUnavailableException ex)
                {
                    bool pending = ex.Reason is SourceUnavailableReason.ProxyPending or SourceUnavailableReason.Opening;
                    return (Id: node.Id, Image: MediaPlaceholder.Get(width, height, ex.Reason), Transient: false, Pending: pending);
                }
            }));

            var media = frames.ToDictionary(f => f.Id, f => (f.Image, f.Transient));
            return (media, !frames.Any(f => f.Pending));
        }

        private (SKImage, bool) ResolveMedia(VideoClip clip, Graph graph, VideoSourceNode node, double clipSeconds, int frameIndex, int canvasWidth, int canvasHeight)
        {
            MediaInput input = Input(clip, node);
            TimeSpan content = TimeSpan.FromSeconds(clipSeconds);

            try
            {
                if (input.Failure is { } failure)
                {
                    if (!RetryDue(input)) throw failure;

                    input.Failure = null;
                    StartPreparing(input);
                }

                IPreparedVideoSource prepared = AwaitPrepared(input);

                if (_options.Buffered)
                {
                    input.Buffer ??= OpenBuffer(input, prepared, graph, frameIndex, ReachEnd(null, clip));
                    return Owned(input.Buffer.Take(frameIndex, content));
                }

                input.Reader ??= prepared.OpenReader(ReaderOptions(input, prepared, graph, content, callerOwnsFrames: false));
                return Owned(input.Reader.GetFrame(content));
            }
            catch (SourceUnavailableException ex)
            {
                return Unavailable(input, ex, frameIndex, canvasWidth, canvasHeight);
            }

            static (SKImage, bool) Owned(VideoFrame frame) => (frame.Image, frame.Transient);
        }

        private (SKImage, bool) Unavailable(MediaInput input, SourceUnavailableException ex, int frameIndex, int canvasWidth, int canvasHeight)
        {
            switch (ex.Reason)
            {
                case SourceUnavailableReason.EndOfSource:
                    break;

                case SourceUnavailableReason.Opening:
                case SourceUnavailableReason.ProxyPending:
                    LastFrameIncomplete = true;
                    RecordProblem(input, ex, frameIndex);
                    break;

                default:
                    if (input.Failure is null)
                    {
                        input.Failure = ex;
                        input.FailedAt = Environment.TickCount64;
                        Release(input);

                        if (_options.Failures == ContentFailurePolicy.Preview)
                            EditSharpConfig.Logger.LogWarning($"{Describe(input.Source)}: {ex.Message} Showing a placeholder.");
                    }

                    RecordProblem(input, ex, frameIndex);
                    break;
            }

            return (MediaPlaceholder.Get(canvasWidth, canvasHeight, ex.Reason), false);
        }

        private void RecordProblem(MediaInput input, SourceUnavailableException ex, int frameIndex)
        {
            if (_options.Report is not { } report) return;

            TimeSpan at = FrameStateResolver.TimeOfFrame(frameIndex, _options.Fps);
            if (report.Record(input.Node.Id, Describe(input.Source), ex.Reason, ex.Message, at))
                EditSharpConfig.Logger.LogWarning($"{Describe(input.Source)}: {ex.Message} Rendering a placeholder from {at}.");
        }

        private bool RetryDue(MediaInput input) =>
            _options.Failures == ContentFailurePolicy.Preview &&
            input.Failure!.Reason is SourceUnavailableReason.MediaOffline or SourceUnavailableReason.DecodeError &&
            Environment.TickCount64 - input.FailedAt >= EditSharpConfig.SourceRetryInterval.TotalMilliseconds;

        // ---------------------------------------------------------------
        // Inputs
        // ---------------------------------------------------------------

        //the node's current source; swapping it mid-session drops everything held for the old one
        private MediaInput Input(VideoClip clip, VideoSourceNode node)
        {
            if (_media.TryGetValue(node.Id, out MediaInput? known) && ReferenceEquals(known.Source, node.Source) && ReferenceEquals(known.Clip, clip))
                return known;

            if (known is not null) Release(known);

            var input = new MediaInput(clip, node, node.Source);
            _media[node.Id] = input;
            return input;
        }

        /// <summary>Starts preparing if nothing is prepared or preparing; returns the task still to finish, if any.</summary>
        private Task<IPreparedVideoSource>? StartPreparing(MediaInput input)
        {
            if (input.Prepared is not null || input.Failure is not null) return null;

            input.Preparing ??= Task.Run(() => input.Source.PrepareAsync(new VideoPrepareContext(_options.HwAccel, _options.SourceMode)));
            return input.Preparing.IsCompleted ? null : input.Preparing;
        }

        /// <summary>The prepared handle if preparing has finished; records a failure (and returns null) if it failed.</summary>
        private IPreparedVideoSource? TryTakePrepared(MediaInput input)
        {
            if (input.Prepared is not null) return input.Prepared;
            if (input.Preparing is not { IsCompleted: true } done) return null;

            input.Preparing = null;

            if (done.IsCompletedSuccessfully) return input.Prepared = done.Result;

            input.Failure = AsUnavailable(done.Exception?.InnerException, input.Source);
            input.FailedAt = Environment.TickCount64;
            return null;
        }

        /// <summary>
        /// The prepared handle, waiting for preparation if needed; except in a
        /// buffered preview, which must never stall on it and shows Opening
        /// instead (WaitReady is where a preview chooses to wait).
        /// </summary>
        private IPreparedVideoSource AwaitPrepared(MediaInput input)
        {
            if (input.Prepared is { } prepared) return prepared;

            Task<IPreparedVideoSource> preparing = input.Preparing ?? StartPreparing(input) ?? input.Preparing!;

            if (!preparing.IsCompleted && _options.Buffered && _options.Failures == ContentFailurePolicy.Preview)
                throw new SourceUnavailableException(SourceUnavailableReason.Opening, $"{Describe(input.Source)} is still opening.");

            WaitQuietly(preparing, Timeout.InfiniteTimeSpan);

            return TryTakePrepared(input) ?? throw input.Failure!;
        }

        private BufferedVideoReader OpenBuffer(MediaInput input, IPreparedVideoSource prepared, Graph graph, int firstFrame, TimeSpan reachEnd)
        {
            VideoClip clip = input.Clip;
            TimeSpan content = ContentTimeOf(clip, firstFrame);

            int firstClipFrame = (int)Math.Ceiling(clip.Start.TotalSeconds * _options.Fps - 1e-9);
            int endFrame = (int)Math.Ceiling(reachEnd.TotalSeconds * _options.Fps - 1e-9);

            IVideoFrameReader reader = prepared.OpenReader(ReaderOptions(input, prepared, graph, content, callerOwnsFrames: true));

            return input.Buffer = new BufferedVideoReader(
                reader, firstFrame, _options.Direction, EditSharpConfig.ReaderBufferFrames,
                frame => ContentTimeOf(clip, frame),
                frame => frame >= firstClipFrame && frame < endFrame);
        }

        private VideoReaderOptions ReaderOptions(MediaInput input, IPreparedVideoSource prepared, Graph graph, TimeSpan startAt, bool callerOwnsFrames)
        {
            //decode only as large as the clip's own transform will ever show it
            (int nativeWidth, int nativeHeight) = prepared.NativeSize;
            ClipTransform transform = DecodeSizeHeuristics.FindDownstreamTransform(graph, input.Node)?.Transform ?? new ClipTransform();

            (int width, int height) = nativeWidth > 0 && nativeHeight > 0
                ? TransformProjection.ComputeContentSize(transform, nativeWidth, nativeHeight, _options.CanvasWidth, _options.CanvasHeight)
                : (0, 0);

            return new VideoReaderOptions(
                _options.ReadMode, startAt, _options.Fps, input.Clip.Speed, width, height, callerOwnsFrames);
        }

        private void Release(MediaInput input)
        {
            input.Buffer?.Dispose();
            input.Buffer = null;
            input.Reader?.Dispose();
            input.Reader = null;
            input.Prepared?.Dispose();
            input.Prepared = null;

            //still preparing: dispose whatever it produces once it's done
            if (input.Preparing is { } preparing)
            {
                input.Preparing = null;
                preparing.ContinueWith(static t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
            }
        }

        // ---------------------------------------------------------------
        // Timeline arithmetic
        // ---------------------------------------------------------------

        private TimeSpan ContentTimeOf(Clip clip, int frame) =>
            TimeSpan.FromSeconds(FrameStateResolver.ClipSecondsAt(clip, FrameStateResolver.TimeOfFrame(frame, _options.Fps)));

        /// <summary>
        /// How far past its own end a clip is still composited: through the
        /// transition into the next clip, if its channel has one (the outgoing
        /// clip is drawn under it).
        /// </summary>
        private static TimeSpan ReachEnd(Channel? channel, Clip clip)
        {
            using var _ = ModelLock.Read();

            channel ??= clip.Channel;
            TimeSpan transition = channel?.Transitions.FirstOrDefault(t => ReferenceEquals(t.From, clip))?.Duration ?? TimeSpan.Zero;
            return clip.End + Max(TimeSpan.Zero, transition);
        }

        //where a clip's buffer starts: the playhead if the clip is already on screen, else where it'll first appear
        private int EntryFrame(Clip clip, TimeSpan reachEnd, int frameIndex)
        {
            int first = (int)Math.Ceiling(clip.Start.TotalSeconds * _options.Fps - 1e-9);
            int last = (int)Math.Ceiling(reachEnd.TotalSeconds * _options.Fps - 1e-9) - 1;

            return _options.Direction > 0 ? Math.Max(first, frameIndex) : Math.Min(last, frameIndex);
        }

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

        private static bool WaitQuietly(Task task, TimeSpan timeout)
        {
            try { return task.Wait(timeout); }
            catch (AggregateException) { return true; } //finished, by failing; TryTakePrepared records it
        }

        private static SourceUnavailableException AsUnavailable(Exception? ex, Source source) => ex as SourceUnavailableException
            ?? new SourceUnavailableException(SourceUnavailableReason.DecodeError, $"{Describe(source)} could not be prepared.", ex);

        /// <summary>A person-readable name for a source: its kind id, and its file if it has one.</summary>
        private static string Describe(Source source)
        {
            string kind = source.GetType().GetCustomAttribute<SourceKindAttribute>()?.Id ?? source.GetType().Name;
            return source is IFileBackedSource file ? $"{kind}: {file.FilePath}" : kind;
        }

        // ---------------------------------------------------------------
        // Not-yet-sources: text and nested timelines
        // ---------------------------------------------------------------

        private (SKImage, bool) GetOrRasterizeText(TextInputNode text, int frameIndex, int canvasWidth, int canvasHeight)
        {
            if (_staticContent.TryGetValue(text.Id, out SKImage? cached)) return (cached, false);

            if (_brokenStatic.Contains(text.Id))
                return (MediaPlaceholder.Get(canvasWidth, canvasHeight, SourceUnavailableReason.DecodeError), false);

            try
            {
                string path = TextRasterizer.Rasterize(text, canvasWidth, canvasHeight, out _, out _);
                _ownedTempFiles[text.Id] = path;

                using SKData data = SKData.Create(path) ?? throw new InvalidOperationException($"Could not read '{path}'.");
                SKImage image = SKImage.FromEncodedData(data) ?? throw new InvalidOperationException($"Could not decode '{path}'.");

                _staticContent[text.Id] = image;
                return (image, false);
            }
            catch (Exception ex)
            {
                _brokenStatic.Add(text.Id);
                EditSharpConfig.Logger.LogWarning($"A text node couldn't be rasterized, showing a placeholder: {ex.Message}");

                _options.Report?.Record(text.Id, "text", SourceUnavailableReason.DecodeError, ex.Message,
                    FrameStateResolver.TimeOfFrame(frameIndex, _options.Fps));

                return (MediaPlaceholder.Get(canvasWidth, canvasHeight, SourceUnavailableReason.DecodeError), false);
            }
        }

        private NestedTimelineRenderer GetOrCreateNestedRenderer(TimelineVideoInputNode embed)
        {
            if (_nestedRenderers.TryGetValue(embed.Id, out NestedTimelineRenderer? existing)) return existing;

            //nested timelines read on demand, in the same mode and under the same failure policy as their parent
            var renderer = new NestedTimelineRenderer(embed.Reference, _options with { Buffered = false, Direction = 1 });
            _nestedRenderers[embed.Id] = renderer;
            return renderer;
        }

        public void Dispose()
        {
            foreach (MediaInput input in _media.Values) Release(input);
            _media.Clear();

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
