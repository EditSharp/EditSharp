using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EditSharp;
using EditSharp.Components;
using EditSharp.Components.Clips;
using EditSharp.Components.Nodes.Sources.Video;
using EditSharp.Composite;

namespace EditSharp.Playback
{
    /// <summary>
    /// Audio-based playback for timelines.
    ///
    /// Built directly on top of Renderer's Skia compositor primitives
    /// (RenderContentPreparation, SkClipContentSource, SkFrameCompositor,
    /// GpuContext, SkSurfacePool) rather than re-deriving them.
    ///
    /// "CLIPS ARE GRAPHS" REWRITE: RenderContentPreparation's
    /// nativeSizes/decodePlans/decodeSourcePaths dictionaries are now keyed
    /// by InputNode Id (Guid), not by Clip — a VideoClip's graph can contain
    /// more than one MediaSourceNode; staticImagePaths is gone entirely (text
    /// rasterization moved into SkClipContentSource itself). The two
    /// pattern-match sites that used to match `VideoClip { Source.Type:
    /// SourceType.Video }` directly (AllVideoClipsRedirectedToCache,
    /// ComputeSeekOffsets) now walk each VideoClip's own graph for its
    /// Video-type MediaSourceNode(s) instead, since Source no longer lives
    /// directly on VideoClip. FrameStateResolver.Resolve no longer takes a
    /// nativeSizes parameter — see its own remarks.
    ///
    /// REWRITE ("channels split by kind"): AllVideoClipsRedirectedToCache and
    /// ComputeSeekOffsets walk timeline.VideoChannels directly now, rather
    /// than timeline.Channels filtered by `is not VideoClip` — Timeline keeps
    /// VideoChannel and AudioChannel as two separate lists (see Timeline.cs's
    /// own remarks). The two SkSurfacePool seedCount call sites below
    /// (VideoLoopAsync, EnsureScrubSessionBaseAsync) now seed off
    /// Timeline.VideoChannels.Count specifically rather than the old mixed
    /// Timeline.Channels.Count — an AudioChannel never needs a GPU-backed
    /// canvas surface, so counting it toward the warm-start heuristic never
    /// bought anything (see SkSurfacePool's own remarks: this is a warm-start
    /// guess, not a hard cap, so this is a correctness/clarity fix, not a
    /// behavior-changing one).
    ///
    /// CONFIG: Timeline + RenderSettings, not a Blueprint — an end consumer
    /// shouldn't have to construct a full render blueprint (with an unused
    /// OutputDirectory) just to play a timeline back. RenderSettings is
    /// shared with Renderer so the two don't carry duplicate, potentially
    /// drifting copies of resolution/framerate/hardware-accelerator config.
    ///
    /// Position IS DELIBERATELY READ-ONLY FROM OUTSIDE. It exists for a
    /// consumer to ask "where is playback actually at right now," not as an
    /// input — a public setter would mean the video loop has to keep
    /// re-checking whether something external changed it out from under a
    /// running session. Starting from a nonzero position is a PARAMETER TO
    /// Play(), not a pre-set on Position — Seek's use case is fully covered
    /// by Play(TimeSpan?) and was removed as a separate method
    /// deliberately, not by accident. Backed by PlaybackReferenceClock while
    /// a session is active, and a locally-held last-known value otherwise —
    /// see the property itself.
    ///
    /// SYNCHRONIZED STARTUP (PlaybackStartGate): the video loop and the
    /// audio engine each need real setup time before either can start
    /// pacing itself, and that setup is asymmetric (video's is heavier —
    /// see PlaybackStartGate's own remarks). Both loops finish their own
    /// setup, signal the gate, and only THEN start pacing.
    ///
    /// AUDIO DELIVERY IS "PLAY THIS NOW," DELIBERATELY NOT PRE-BUFFERED.
    ///
    /// PLAYBACKMODE / LEADER-FOLLOWER (PlaybackReferenceClock): exactly one
    /// of video/audio is the LEADER for a given session — it paces itself
    /// on its own real Stopwatch and reports its delivered position into a
    /// shared PlaybackReferenceClock. The other stream (if any) is the
    /// FOLLOWER — instead of its own Stopwatch, it polls the reference
    /// clock and only delivers once the leader has actually reached that
    /// content position.
    ///
    /// PAUSE VS STOP (PlaybackPauseGate): genuinely different operations.
    ///
    /// OPTIMIZED-MEDIA CACHE (OptimizedMediaCache, EditSharp.Composite): a
    /// video clip's decoder here may open against a persistent, content-
    /// addressed proxy instead of the clip's true original source file,
    /// whenever RenderContentPreparation.ProbeVideoAsync finds one already
    /// built and big enough.
    ///
    /// SCRUBBING (SupportsScrubbing / RefreshScrubbingSupportAsync /
    /// ScrubToAsync / EndScrubbing): a second, separate playback surface
    /// for "render me one frame at this arbitrary position, right now."
    ///
    /// KNOWN GAPS — tracked, not hidden:
    ///   1. SPEED &lt;= 0 (reverse playback) is NOT supported yet.
    ///   2. ARBITRARY SPEED (audio tracking Speed != 1) is a MUST-HAVE, not
    ///      deferred-maybe.
    ///   3. Seeking to a nonzero start position may need the audio
    ///      composition itself to carry a seek offset.
    ///   4. TRUE FRAME-SKIPPING remains open.
    ///   5. PlaybackMode WIRING — CLOSED (SyncToAudio/EveryFrame both real).
    ///   6. SCRUBBING — CLOSED for the all-optimized-media, forward-or-
    ///      small-jump case.
    /// </summary>
    public class Playback : IDisposable
    {
        public required Timeline Timeline;

        public required RenderSettings RenderSettings;

        public PlaybackMode PlaybackMode = PlaybackMode.SyncToAudio;

        public float Speed = 1f;

        private TimeSpan _lastKnownPosition = TimeSpan.Zero;
        private PlaybackReferenceClock? _referenceClock;
        public TimeSpan Position => _referenceClock?.Position ?? _lastKnownPosition;

        public bool IsPlaying => _isPlaying;

        public bool IsPaused => _pauseGate?.IsPaused ?? false;

        public bool SupportsScrubbing { get; private set; }

        public event EventHandler<AudioSampleEventArgs>? AudioSample;

        protected virtual void OnAudioSample(AudioSampleEventArgs e)
        {
            AudioSample?.Invoke(this, e);
        }

        public event EventHandler<VideoFrameEventArgs>? VideoFrame;

        protected virtual void OnVideoFrame(VideoFrameEventArgs e)
        {
            VideoFrame?.Invoke(this, e);
        }

        public event EventHandler? EndReached;

        protected virtual void OnEndReached(EventArgs e)
        {
            EndReached?.Invoke(this, e);
        }

        public event EventHandler? PlaybackStarted;

        protected virtual void OnPlaybackStarted(EventArgs e)
        {
            PlaybackStarted?.Invoke(this, e);
        }

        private readonly object _stateLock = new();
        private bool _isPlaying;
        private CancellationTokenSource? _cts;
        private Task? _videoTask;
        private PlaybackAudioEngine? _audioEngine;
        private PlaybackPauseGate? _pauseGate;

        private const int ScrubForwardStepBudgetFrames = 15;

        private readonly SemaphoreSlim _scrubGate = new(1, 1);
        private GpuContext? _scrubGpuContext;
        private SkSurfacePool? _scrubSurfacePool;
        private ConcurrentDictionary<Guid, (int, int)>? _scrubNativeSizes;
        private ConcurrentDictionary<Guid, DecodeHwAccelPlan>? _scrubDecodePlans;
        private ConcurrentDictionary<Guid, string>? _scrubDecodeSourcePaths;
        private Dictionary<int, List<Clip>>? _scrubDecoderReleaseSchedule;

        private SkClipContentSource? _scrubContentSource;
        private int? _scrubLastFrameIndex;
        private byte[]? _scrubLastDeliveredBuffer;
        private int _scrubLastDeliveredLength;

        public void Play(TimeSpan? startPosition = null)
        {
            lock (_stateLock)
            {
                if (_isPlaying && startPosition == null)
                {
                    _pauseGate?.Resume();
                    _referenceClock?.ResumeWallClock();
                    return;
                }
            }

            if (_isPlaying) Stop();

            lock (_stateLock)
            {
                if (_isPlaying) return;

                if (Speed <= 0f)
                    throw new NotSupportedException(
                        "Playback.Speed <= 0 (reverse or stopped-via-speed) is not supported yet " +
                        "— see Playback's class remarks, gap 1.");

                if (Timeline.Channels.Count == 0)
                    throw new ArgumentException("Timeline must contain at least one Channel.");

                TimeSpan resolvedStart = startPosition ?? Position;

                if (resolvedStart < TimeSpan.Zero || resolvedStart > Timeline.Duration)
                    throw new ArgumentOutOfRangeException(nameof(startPosition),
                        $"startPosition must be within [0, {Timeline.Duration}].");

                _isPlaying = true;
                _cts = new CancellationTokenSource();
                CancellationToken token = _cts.Token;

                var pauseGate = new PlaybackPauseGate();
                _pauseGate = pauseGate;

                var referenceClock = new PlaybackReferenceClock();
                referenceClock.Report(resolvedStart);
                _referenceClock = referenceClock;

                bool audioParticipates = Math.Abs(Speed - 1f) < 0.0001f;

                bool videoFollows = audioParticipates && PlaybackMode == PlaybackMode.SyncToAudio;
                bool audioFollows = audioParticipates && PlaybackMode == PlaybackMode.EveryFrame;

                var startGate = new PlaybackStartGate(
                    audioParticipates ? 2 : 1,
                    onReleased: () => OnPlaybackStarted(EventArgs.Empty));

                _videoTask = Task.Run(
                    () => VideoLoopAsync(token, resolvedStart, startGate, pauseGate, referenceClock, videoFollows),
                    token);

                if (audioParticipates)
                {
                    var audioEngine = new PlaybackAudioEngine();
                    _audioEngine = audioEngine;

                    _ = audioEngine
                        .StartAsync(
                            Timeline, RenderSettings.Framerate,
                            (int)RenderSettings.Resolution.X, (int)RenderSettings.Resolution.Y,
                            resolvedStart, startGate, pauseGate,
                            referenceClock, audioFollows, args => OnAudioSample(args), token)
                        .ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                                EditSharpConfig.Logger.Log(
                                    $"Playback audio engine failed to start: {t.Exception}");
                        }, TaskScheduler.Default);
                }
                else
                {
                    EditSharpConfig.Logger.LogVerbose(
                        $"Speed={Speed} != 1 — audio is not played this session (see Playback's " +
                        "class remarks, gap 2).");
                }
            }
        }

        public void Pause()
        {
            lock (_stateLock)
            {
                if (!_isPlaying || _pauseGate == null) return;
                _pauseGate.Pause();
                _referenceClock?.PauseWallClock();
            }

            EditSharpConfig.Logger.LogVerbose("Playback paused.");
        }

        public void Stop()
        {
            CancellationTokenSource? cts;

            lock (_stateLock)
            {
                if (!_isPlaying) return;
                _isPlaying = false;
                cts = _cts;
                _cts = null;
                _pauseGate = null;

                _lastKnownPosition = _referenceClock?.Position ?? _lastKnownPosition;
                _referenceClock = null;
            }

            cts?.Cancel();

            try { _videoTask?.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { /* expected */ }

            _audioEngine?.Dispose();
            _audioEngine = null;

            cts?.Dispose();
            _videoTask = null;
        }

        public async Task<bool> RefreshScrubbingSupportAsync(CancellationToken ct = default)
        {
            bool supported = await RenderContentPreparation.AllVideoSourcesHaveSufficientCachedMediaAsync(
                Timeline, (int)RenderSettings.Resolution.X, (int)RenderSettings.Resolution.Y);

            ct.ThrowIfCancellationRequested();

            SupportsScrubbing = supported;
            return supported;
        }

        public async Task ScrubToAsync(TimeSpan position, CancellationToken ct = default)
        {
            if (!SupportsScrubbing)
                throw new InvalidOperationException(
                    "ScrubToAsync requires SupportsScrubbing — not every video source in Timeline " +
                    "currently has sufficient cached optimized media. Prewarm the missing sources " +
                    "via OptimizedMediaCache.PrewarmAsync and call RefreshScrubbingSupportAsync again.");

            lock (_stateLock)
            {
                if (_isPlaying && !(_pauseGate?.IsPaused ?? false))
                    throw new InvalidOperationException(
                        "ScrubToAsync cannot be used while actively playing — Pause() first.");
            }

            if (position < TimeSpan.Zero || position > Timeline.Duration)
                throw new ArgumentOutOfRangeException(nameof(position),
                    $"position must be within [0, {Timeline.Duration}].");

            int width = (int)RenderSettings.Resolution.X;
            int height = (int)RenderSettings.Resolution.Y;
            int fps = RenderSettings.Framerate;

            await _scrubGate.WaitAsync(ct);
            try
            {
                await EnsureScrubSessionBaseAsync(width, height, ct);

                int targetFrameIndex = (int)(position.TotalSeconds * fps);

                if (targetFrameIndex == _scrubLastFrameIndex && _scrubLastDeliveredBuffer != null)
                {
                    OnVideoFrame(new VideoFrameEventArgs(
                        _scrubLastDeliveredBuffer, _scrubLastDeliveredLength, width, height, position));
                    return;
                }

                bool needsReopen =
                    _scrubContentSource == null ||
                    _scrubLastFrameIndex == null ||
                    targetFrameIndex < _scrubLastFrameIndex.Value ||
                    targetFrameIndex - _scrubLastFrameIndex.Value > ScrubForwardStepBudgetFrames;

                int fromFrameIndex;

                if (needsReopen)
                {
                    _scrubContentSource?.Dispose();

                    Dictionary<Clip, TimeSpan> seekOffsets = ComputeSeekOffsets(Timeline, position);

                    _scrubContentSource = new SkClipContentSource(
                        fps, RenderSettings.HardwareAccelerator,
                        _scrubNativeSizes!, _scrubDecodePlans!,
                        seekOffsets, _scrubDecodeSourcePaths!);

                    fromFrameIndex = targetFrameIndex;
                }
                else
                {
                    fromFrameIndex = _scrubLastFrameIndex!.Value + 1;
                }

                SkClipContentSource contentSource = _scrubContentSource;

                byte[]? pooledBuffer = null;
                int length = 0;

                for (int frameIndex = fromFrameIndex; frameIndex <= targetFrameIndex; frameIndex++)
                {
                    FrameState state = FrameStateResolver.Resolve(Timeline, frameIndex, fps);

                    (byte[] buffer, int bufLength) = SkFrameCompositor.RenderFrame(
                        state, contentSource, width, height, fps, _scrubSurfacePool!);

                    if (frameIndex == targetFrameIndex)
                    {
                        pooledBuffer = buffer;
                        length = bufLength;
                    }
                    else
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    if (_scrubDecoderReleaseSchedule!.TryGetValue(frameIndex, out List<Clip>? finished))
                    {
                        foreach (Clip clip in finished) contentSource.ReleaseDecoder(clip);
                    }
                }

                _scrubLastFrameIndex = targetFrameIndex;

                try
                {
                    _scrubLastDeliveredBuffer = pooledBuffer![..length];
                    _scrubLastDeliveredLength = length;

                    OnVideoFrame(new VideoFrameEventArgs(pooledBuffer, length, width, height, position));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(pooledBuffer!);
                }

                lock (_stateLock) { _lastKnownPosition = position; }
            }
            finally
            {
                _scrubGate.Release();
            }
        }

        private async Task EnsureScrubSessionBaseAsync(int width, int height, CancellationToken ct)
        {
            if (_scrubGpuContext != null) return;

            var nativeSizes = new ConcurrentDictionary<Guid, (int, int)>();
            var decodePlans = new ConcurrentDictionary<Guid, DecodeHwAccelPlan>();
            var decodeSourcePaths = new ConcurrentDictionary<Guid, string>();

            await RenderContentPreparation.PrepareContentAsync(
                Timeline, width, height, RenderSettings.HardwareAccelerator,
                nativeSizes, decodePlans, decodeSourcePaths);

            ct.ThrowIfCancellationRequested();

            _scrubNativeSizes = nativeSizes;
            _scrubDecodePlans = decodePlans;
            _scrubDecodeSourcePaths = decodeSourcePaths;
            _scrubDecoderReleaseSchedule =
                RenderContentPreparation.BuildDecoderReleaseSchedule(Timeline, RenderSettings.Framerate);

            _scrubGpuContext = GpuContext.Create(
                RenderSettings.HardwareAccelerator, RenderSettings.GpuAdapterIndex);
            _scrubSurfacePool = new SkSurfacePool(
                _scrubGpuContext.GRContext, width, height, Timeline.VideoChannels.Count);
        }

        public void EndScrubbing()
        {
            _scrubGate.Wait();
            try
            {
                _scrubContentSource?.Dispose();
                _scrubContentSource = null;
                _scrubLastFrameIndex = null;
                _scrubLastDeliveredBuffer = null;
                _scrubLastDeliveredLength = 0;

                _scrubSurfacePool?.Dispose();
                _scrubSurfacePool = null;

                _scrubGpuContext?.Dispose();
                _scrubGpuContext = null;

                _scrubNativeSizes = null;
                _scrubDecodePlans = null;
                _scrubDecodeSourcePaths = null;
                _scrubDecoderReleaseSchedule = null;
            }
            finally
            {
                _scrubGate.Release();
            }
        }

        /// <summary>
        /// BUG FOUND IN THE FIELD (fixed here): setup (PrepareContentAsync,
        /// GpuContext.Create, SkSurfacePool construction, the warm-up
        /// render) used to run with no surrounding try/catch of its own —
        /// any exception there propagated straight out of this Task.Run'd
        /// method without ever calling `startGate.Fault(...)`. When audio
        /// participates, PlaybackStartGate requires BOTH participants to
        /// reach ReadyAndWaitAsync before either is released (see its own
        /// remarks) — if video's setup throws before it ever gets there,
        /// the audio engine's own await on the same gate hangs forever,
        /// since nothing was left to complete or fault it. Fixed by wrapping
        /// the whole loop and calling Fault() on any failure that isn't an
        /// expected OperationCanceledException from Stop()/Dispose().
        /// </summary>
        private async Task VideoLoopAsync(
            CancellationToken token, TimeSpan startPosition,
            PlaybackStartGate startGate, PlaybackPauseGate pauseGate,
            PlaybackReferenceClock referenceClock, bool followsReferenceClock)
        {
            int width = (int)RenderSettings.Resolution.X;
            int height = (int)RenderSettings.Resolution.Y;
            int fps = RenderSettings.Framerate;

            var nativeSizes = new ConcurrentDictionary<Guid, (int, int)>();
            var decodePlans = new ConcurrentDictionary<Guid, DecodeHwAccelPlan>();
            var decodeSourcePaths = new ConcurrentDictionary<Guid, string>();

            try
            {
                try
                {
                    await RenderContentPreparation.PrepareContentAsync(
                        Timeline, width, height, RenderSettings.HardwareAccelerator,
                        nativeSizes, decodePlans, decodeSourcePaths);

                    SupportsScrubbing = AllVideoClipsRedirectedToCache(Timeline, decodeSourcePaths);

                    Dictionary<Clip, TimeSpan> seekOffsets = ComputeSeekOffsets(Timeline, startPosition);

                    Dictionary<int, List<Clip>> decoderReleaseSchedule =
                        RenderContentPreparation.BuildDecoderReleaseSchedule(Timeline, fps);

                    using var contentSource = new SkClipContentSource(
                        fps, RenderSettings.HardwareAccelerator, nativeSizes, decodePlans, seekOffsets, decodeSourcePaths);

                    using GpuContext gpuContext = GpuContext.Create(
                        RenderSettings.HardwareAccelerator, RenderSettings.GpuAdapterIndex);
                    using var surfacePool = new SkSurfacePool(
                        gpuContext.GRContext, width, height, Timeline.VideoChannels.Count);

                    int startFrame = (int)(startPosition.TotalSeconds * fps);
                    int totalFrames = Math.Max(1, (int)Math.Ceiling(Timeline.Duration.TotalSeconds * fps));

                    FrameState warmupState = FrameStateResolver.Resolve(Timeline, startFrame, fps);
                    (byte[] warmupBuffer, int warmupLength) = SkFrameCompositor.RenderFrame(
                        warmupState, contentSource, width, height, fps, surfacePool);
                    EditSharpConfig.Logger.LogVerbose("Video warm-up frame rendered.");

                    await startGate.ReadyAndWaitAsync(token);

                    Stopwatch? clock = followsReferenceClock ? null : Stopwatch.StartNew();
                    EditSharpConfig.Logger.LogVerbose(followsReferenceClock
                        ? "Video now following the reference clock."
                        : "Video pacing clock started.");

                    if (!followsReferenceClock) referenceClock.Report(startPosition);
                    try
                    {
                        OnVideoFrame(new VideoFrameEventArgs(warmupBuffer, warmupLength, width, height, startPosition));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(warmupBuffer);
                    }

                    if (decoderReleaseSchedule.TryGetValue(startFrame, out List<Clip>? finishedAtStart))
                    {
                        foreach (Clip clip in finishedAtStart) contentSource.ReleaseDecoder(clip);
                    }

                    for (int frameIndex = startFrame + 1; frameIndex < totalFrames; frameIndex++)
                    {
                        TimeSpan frameOffset = TimeSpan.FromSeconds((frameIndex - startFrame) / (double)fps);
                        TimeSpan framePosition = startPosition + frameOffset;

                        while (true)
                        {
                            if (token.IsCancellationRequested) return;

                            if (pauseGate.IsPaused)
                            {
                                clock?.Stop();
                                try { await pauseGate.WaitIfPausedAsync(token); }
                                catch (OperationCanceledException) { return; }
                                clock?.Start();
                                continue;
                            }

                            if (followsReferenceClock)
                            {
                                TimeSpan gap = framePosition - referenceClock.Position;

                                if (gap > TimeSpan.Zero)
                                {
                                    TimeSpan wait = gap > PlaybackReferenceClock.PollInterval
                                        ? gap : PlaybackReferenceClock.PollInterval;

                                    try { await Task.Delay(wait, token); }
                                    catch (OperationCanceledException) { return; }
                                    continue;
                                }
                            }

                            break;
                        }

                        FrameState state = FrameStateResolver.Resolve(Timeline, frameIndex, fps);

                        (byte[] buffer, int length) = SkFrameCompositor.RenderFrame(
                            state, contentSource, width, height, fps, surfacePool);

                        if (!followsReferenceClock)
                        {
                            TimeSpan targetElapsed = TimeSpan.FromSeconds(frameOffset.TotalSeconds / Speed);
                            TimeSpan actualElapsed = clock!.Elapsed;

                            if (targetElapsed > actualElapsed)
                            {
                                try { await Task.Delay(targetElapsed - actualElapsed, token); }
                                catch (OperationCanceledException)
                                {
                                    ArrayPool<byte>.Shared.Return(buffer);
                                    return;
                                }
                            }

                            referenceClock.Report(framePosition);
                        }

                        try
                        {
                            OnVideoFrame(new VideoFrameEventArgs(buffer, length, width, height, framePosition));
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }

                        if (decoderReleaseSchedule.TryGetValue(frameIndex, out List<Clip>? finished))
                        {
                            foreach (Clip clip in finished) contentSource.ReleaseDecoder(clip);
                        }
                    }

                    TearDownAfterNaturalEnd();

                    OnEndReached(EventArgs.Empty);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Setup (or an otherwise-unhandled mid-loop failure)
                    // blew up — release whichever partner (audio, if
                    // participating) is still waiting at the start gate
                    // instead of leaving it to hang forever. A no-op if the
                    // gate already opened normally.
                    startGate.Fault(ex);
                    throw;
                }
            }
            finally
            {
                lock (_stateLock) { _isPlaying = false; }
            }
        }

        /// <summary>
        /// Cheap, no-I/O check: true when every Video-type MediaSourceNode
        /// across every VideoClip in `timeline` was redirected in
        /// `decodeSourcePaths`.
        /// </summary>
        private static bool AllVideoClipsRedirectedToCache(
            Timeline timeline, IReadOnlyDictionary<Guid, string> decodeSourcePaths)
        {
            foreach (VideoChannel channel in timeline.VideoChannels)
            {
                foreach (Clip clip in channel.Clips)
                {
                    if (clip is not VideoClip video) continue;

                    foreach (MediaSourceNode media in video.Graph.Nodes.OfType<MediaSourceNode>())
                    {
                        if (media.Source.Type != SourceType.Video) continue;

                        if (!decodeSourcePaths.TryGetValue(media.Id, out string? decodePath)) return false;
                        if (decodePath == media.Source.Path) return false;
                    }
                }
            }

            return true;
        }

        private void TearDownAfterNaturalEnd()
        {
            CancellationTokenSource? cts;

            lock (_stateLock)
            {
                if (!_isPlaying) return;

                _isPlaying = false;
                _lastKnownPosition = _referenceClock?.Position ?? _lastKnownPosition;
                _referenceClock = null;
                _pauseGate = null;
                cts = _cts;
                _cts = null;
            }

            _audioEngine?.Dispose();
            _audioEngine = null;

            cts?.Dispose();
            _videoTask = null;
        }

        /// <summary>
        /// For every video clip already visible at `position`, the
        /// additional offset (beyond the clip's own trim start) its
        /// decoder(s) need to open at. Still keyed by Clip, not by node — a
        /// clip's media inputs all start that same amount further in,
        /// regardless of how many it has (see SkClipContentSource.GetOrOpenDecoder).
        /// </summary>
        private static Dictionary<Clip, TimeSpan> ComputeSeekOffsets(Timeline timeline, TimeSpan position)
        {
            var offsets = new Dictionary<Clip, TimeSpan>();

            foreach (VideoChannel channel in timeline.VideoChannels)
            {
                foreach (Clip clip in channel.Clips)
                {
                    if (clip is not VideoClip video) continue;
                    if (!video.Graph.Nodes.OfType<MediaSourceNode>().Any(m => m.Source.Type == SourceType.Video)) continue;
                    if (position < clip.Start || position >= clip.End) continue;

                    offsets[clip] = position - clip.Start;
                }
            }

            return offsets;
        }

        public void Dispose()
        {
            Stop();
            EndScrubbing();
        }
    }
}