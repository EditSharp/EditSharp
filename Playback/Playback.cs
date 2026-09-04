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
    /// more than one VideoSourceNode; staticImagePaths is gone entirely (text
    /// rasterization moved into SkClipContentSource itself). The two
    /// pattern-match sites that used to match `VideoClip { Source.Type:
    /// SourceType.Video }` directly (AllVideoClipsRedirectedToCache,
    /// ComputeSeekOffsets) now walk each VideoClip's own graph for its
    /// Video-type VideoSourceNode(s) instead, since Source no longer lives
    /// directly on VideoClip. FrameStateResolver.Resolve no longer takes a
    /// nativeSizes parameter — see its own remarks.
    ///
    /// REWRITE ("channels split by kind"): AllVideoClipsRedirectedToCache and
    /// ComputeSeekOffsets walk timeline.VideoChannels directly now, rather
    /// than timeline.Channels filtered by `is not VideoClip` — Timeline keeps
    /// VideoChannel and AudioChannel as two separate lists (see Timeline.cs's
    /// own remarks). The two SkSurfacePool seedCount call sites below
    /// (VideoLoopAsync, ReverseVideoLoopAsync) now seed off
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
    /// content position. Reverse playback never has a follower — audio never
    /// participates in a reverse session (see REVERSE PLAYBACK below), so
    /// ReverseVideoLoopAsync is always the sole leader of its own session.
    ///
    /// PAUSE VS STOP (PlaybackPauseGate): genuinely different operations.
    ///
    /// I-FRAME-ONLY SCRUB/REVERSE (KeyframeIndex / ScrubFrameSource /
    /// SkSourceDecoder.DecodeSingleFrameAsync): ScrubToAsync and reverse
    /// playback (Speed &lt; 0) share ONE mechanism, deliberately — both need
    /// "show me approximately this arbitrary position, instantly, with no
    /// buffering," which is exactly what jumping straight to the nearest
    /// I-frame gives for free (every frame is independently seekable at
    /// the container level; nothing else needs decoding to reach it).
    /// Neither depends on OptimizedMediaCache/pre-built proxy media any
    /// more — both decode straight from each clip's own original source via
    /// a per-source keyframe-timestamp index (KeyframeIndex, one ffprobe
    /// scan, cached) and a one-shot ffmpeg decode seeked to an exact
    /// keyframe timestamp (SkSourceDecoder.DecodeSingleFrameAsync). The
    /// trade-off, named not hidden: what's delivered is accurate to the
    /// NEAREST KEYFRAME, not the exact requested frame/time — normal for a
    /// scrub/rewind preview, and for reverse playback specifically this
    /// means the visual character is a genuine "fast rewind" (jumps between
    /// keyframes) rather than smooth backward motion — see
    /// ReverseVideoLoopAsync's own remarks. Forward playback (VideoLoopAsync)
    /// is UNCHANGED by this — it still uses SkClipContentSource's persistent
    /// forward-only pipe (and still opportunistically benefits from
    /// OptimizedMediaCache when one already exists), since it has no need
    /// for arbitrary-position access at all.
    ///
    /// SCRUB COALESCING ("LATEST REQUEST WINS") — FOUND IN THE FIELD, FIXED:
    /// a fast scrub drag firing many ScrubToAsync calls used to serialize
    /// them FIFO behind _scrubGate — every call, even one whose target
    /// position was already stale by the time it started, ran a real ffmpeg
    /// decode to completion before the next could even begin. Worse, the
    /// one-time per-session setup (native size/decode-plan probing across
    /// every video source) used to be gated by that SAME per-call
    /// cancellation token: a request superseded mid-setup aborted setup
    /// entirely, and the next request restarted it from scratch — under a
    /// fast enough drag, setup could keep getting killed and restarted
    /// forever, completing NEVER, which is what actually froze the whole
    /// pipeline (spinning on setup, delivering nothing) rather than merely
    /// looking sluggish. Fixed with two independent pieces:
    ///   1. ScrubToAsync now cancels any still-in-flight/queued PREVIOUS
    ///      scrub request the moment a new one arrives (_scrubSupersedeCts),
    ///      linked with the caller's own `ct` — a superseded request's
    ///      OperationCanceledException is swallowed (not the caller's own
    ///      cancellation, so nothing to propagate), and its cancellation now
    ///      genuinely kills any in-flight ffmpeg process (see
    ///      SkSourceDecoder.DecodeSingleFrameAsync's own remarks) instead of
    ///      leaving it to run to completion for a frame nobody wants anymore.
    ///   2. The one-time scrub-session setup is now MEMOIZED
    ///      (_scrubSetupTask, mirroring KeyframeIndex's own cached-Task
    ///      pattern) rather than being started fresh — and abortable —
    ///      inside every call. Once started it always runs to completion
    ///      regardless of which caller kicked it off or whether that caller
    ///      later gets superseded; every call just awaits (cancellably, via
    ///      WaitAsync) whatever the current attempt is. This is exactly the
    ///      same "cancel the WAIT, not the shared WORK" split
    ///      KeyframeIndex.GetAsync already uses for its own cached probe.
    ///
    /// REVERSE PLAYBACK (Speed &lt; 0): Play() branches to
    /// ReverseVideoLoopAsync instead of VideoLoopAsync. Audio never
    /// participates (audioParticipates requires Speed == 1, which negative
    /// Speed never satisfies) — reverse is video-only for now; revisit once
    /// arbitrary-speed forward audio exists and there's a real reversed-PCM
    /// delivery path to build on. Speed == 0 remains unsupported (that's
    /// Pause()/Stop(), not a playback rate) — only that one case still
    /// throws from Play(); any negative Speed is now a normal input.
    ///
    /// OPTIMIZED-MEDIA CACHE (OptimizedMediaCache, EditSharp.Composite): a
    /// video clip's decoder in the FORWARD playback path may open against a
    /// persistent, content-addressed proxy instead of the clip's true
    /// original source file, whenever RenderContentPreparation.
    /// ProbeVideoAsync finds one already built and big enough. Scrubbing and
    /// reverse playback no longer consult this cache at all — see
    /// I-FRAME-ONLY SCRUB/REVERSE above.
    ///
    /// KNOWN GAPS — tracked, not hidden:
    ///   1. REVERSE AUDIO is not implemented — reverse is video-only, see
    ///      REVERSE PLAYBACK above. Video itself IS supported (I-frame-only).
    ///   2. ARBITRARY SPEED (audio tracking Speed != 1, Speed &gt; 0) remains
    ///      a MUST-HAVE, not deferred-maybe.
    ///   3. Seeking to a nonzero start position may need the audio
    ///      composition itself to carry a seek offset.
    ///   4. TRUE FRAME-SKIPPING remains open.
    ///   5. PlaybackMode WIRING — CLOSED (SyncToAudio/EveryFrame both real).
    ///   6. SCRUBBING — CLOSED, and no longer conditional on optimized media
    ///      at all (see I-FRAME-ONLY SCRUB/REVERSE) — SupportsScrubbing is
    ///      always true now; the property/RefreshScrubbingSupportAsync are
    ///      kept only for API compatibility with existing callers. Rapid
    ///      scrub requests are coalesced ("latest wins" — see SCRUB
    ///      COALESCING above), not queued.
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

        // Always true now — see class remarks, I-FRAME-ONLY SCRUB/REVERSE.
        // Kept (rather than removed) purely for API compatibility with
        // existing callers that gate ScrubToAsync on this; safe to stop
        // checking it.
        public bool SupportsScrubbing { get; private set; } = true;

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

        private readonly SemaphoreSlim _scrubGate = new(1, 1);
        private GpuContext? _scrubGpuContext;
        private SkSurfacePool? _scrubSurfacePool;
        private ConcurrentDictionary<Guid, (int, int)>? _scrubNativeSizes;
        private ConcurrentDictionary<Guid, DecodeHwAccelPlan>? _scrubDecodePlans;
        private ScrubFrameSource? _scrubContentSource;

        // Memoized one-time scrub-session setup — see class remarks, SCRUB
        // COALESCING. Started at most once; every ScrubToAsync call awaits
        // whichever attempt is current rather than starting its own.
        private Task? _scrubSetupTask;

        // The most recent ScrubToAsync call's own supersession token — see
        // class remarks, SCRUB COALESCING. Cancelled (and replaced) every
        // time a new ScrubToAsync call arrives, so an older, now-stale
        // request stops waiting/decoding promptly instead of queueing
        // behind _scrubGate.
        private CancellationTokenSource? _scrubSupersedeCts;

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

                if (Speed == 0f)
                    throw new NotSupportedException(
                        "Playback.Speed cannot be 0 — that's Pause()/Stop(), not a playback rate. " +
                        "Negative Speed (reverse) is supported — see Playback's class remarks.");

                if (Timeline.Channels.Count == 0)
                    throw new ArgumentException("Timeline must contain at least one Channel.");

                TimeSpan resolvedStart = startPosition ?? Position;

                if (resolvedStart < TimeSpan.Zero || resolvedStart > Timeline.Duration)
                    throw new ArgumentOutOfRangeException(nameof(startPosition),
                        $"startPosition must be within [0, {Timeline.Duration}].");

                bool reverse = Speed < 0f;

                _isPlaying = true;
                _cts = new CancellationTokenSource();
                CancellationToken token = _cts.Token;

                var pauseGate = new PlaybackPauseGate();
                _pauseGate = pauseGate;

                var referenceClock = new PlaybackReferenceClock();
                referenceClock.Report(resolvedStart);
                _referenceClock = referenceClock;

                // Reverse never participates with audio — see class remarks,
                // REVERSE PLAYBACK. Math.Abs(Speed - 1f) is never < 0.0001f
                // for a negative Speed, so this falls out naturally.
                bool audioParticipates = Math.Abs(Speed - 1f) < 0.0001f;

                bool videoFollows = audioParticipates && PlaybackMode == PlaybackMode.SyncToAudio;
                bool audioFollows = audioParticipates && PlaybackMode == PlaybackMode.EveryFrame;

                var startGate = new PlaybackStartGate(
                    audioParticipates ? 2 : 1,
                    onReleased: () => OnPlaybackStarted(EventArgs.Empty));

                _videoTask = Task.Run(
                    () => reverse
                        ? ReverseVideoLoopAsync(token, resolvedStart, startGate, pauseGate, referenceClock)
                        : VideoLoopAsync(token, resolvedStart, startGate, pauseGate, referenceClock, videoFollows),
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
                        "class remarks, gap 2 / REVERSE PLAYBACK).");
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

        /// <summary>
        /// Always succeeds now — see class remarks, I-FRAME-ONLY SCRUB/
        /// REVERSE. Kept for API compatibility with existing callers that
        /// gate ScrubToAsync on this; safe to stop calling.
        /// </summary>
        public Task<bool> RefreshScrubbingSupportAsync(CancellationToken ct = default)
        {
            SupportsScrubbing = true;
            return Task.FromResult(true);
        }

        /// <summary>
        /// Renders and delivers one frame at `position`, via VideoFrame.
        /// SAFE TO CALL RAPIDLY — e.g. once per pointer-move during a
        /// scrubber drag: each call supersedes (cancels) whatever previous
        /// call hasn't finished yet, so only the LATEST requested position
        /// ever actually completes and gets delivered — see class remarks,
        /// SCRUB COALESCING. A superseded call's Task completes normally
        /// (no exception) rather than throwing — only `ct` (if the CALLER
        /// explicitly cancels it) propagates as a real cancellation.
        /// </summary>
        public async Task ScrubToAsync(TimeSpan position, CancellationToken ct = default)
        {
            lock (_stateLock)
            {
                if (_isPlaying && !(_pauseGate?.IsPaused ?? false))
                    throw new InvalidOperationException(
                        "ScrubToAsync cannot be used while actively playing — Pause() first.");
            }

            if (position < TimeSpan.Zero || position > Timeline.Duration)
                throw new ArgumentOutOfRangeException(nameof(position),
                    $"position must be within [0, {Timeline.Duration}].");

            var supersedeCts = new CancellationTokenSource();
            CancellationTokenSource? previous = Interlocked.Exchange(ref _scrubSupersedeCts, supersedeCts);
            if (previous != null)
            {
                previous.Cancel();
                previous.Dispose();
            }

            using CancellationTokenSource linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(ct, supersedeCts.Token);
            CancellationToken linked = linkedCts.Token;

            int width = (int)RenderSettings.Resolution.X;
            int height = (int)RenderSettings.Resolution.Y;
            int fps = RenderSettings.Framerate;

            try
            {
                await _scrubGate.WaitAsync(linked);
                try
                {
                    // .WaitAsync(linked) — cancellable WAITING only, never
                    // cancels the shared setup itself. See class remarks,
                    // SCRUB COALESCING, point 2.
                    await EnsureScrubSessionBaseAsync(width, height).WaitAsync(linked);

                    (byte[] buffer, int length) = await ComposeInstantFrameAsync(
                        _scrubContentSource!, _scrubSurfacePool!, position, width, height, fps, linked);

                    try
                    {
                        OnVideoFrame(new VideoFrameEventArgs(buffer, length, width, height, position));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    lock (_stateLock) { _lastKnownPosition = position; }
                }
                finally
                {
                    _scrubGate.Release();
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Superseded by a newer ScrubToAsync call — not the
                // caller's own cancellation, so nothing to propagate. See
                // class remarks, SCRUB COALESCING.
            }
        }

        /// <summary>
        /// Resolves the FrameState at `position`, prefetches every visible
        /// VideoClip through `contentSource` (see ScrubFrameSource's own
        /// remarks on why prefetching is a separate async step from the
        /// synchronous composite below), then composites exactly one frame.
        /// Shared by ScrubToAsync (on demand) and ReverseVideoLoopAsync (on
        /// its own pacing timer) — both are, mechanically, "compose one
        /// frame at an arbitrary position, instantly."
        /// </summary>
        private async Task<(byte[] Buffer, int Length)> ComposeInstantFrameAsync(
            ScrubFrameSource contentSource, SkSurfacePool pool,
            TimeSpan position, int width, int height, int fps, CancellationToken ct = default)
        {
            int frameIndex = (int)(position.TotalSeconds * fps);
            FrameState state = FrameStateResolver.Resolve(Timeline, frameIndex, fps);

            foreach (FrameChannel channel in state.Channels)
            {
                foreach (FrameClip frameClip in channel.Clips)
                {
                    if (frameClip.Clip is VideoClip videoClip)
                    {
                        await contentSource.PrefetchAsync(
                            videoClip, frameClip.ClipSeconds, width, height, pool, ct);
                    }
                }
            }

            (byte[] buffer, int length) = SkFrameCompositor.RenderFrame(
                state, contentSource, width, height, fps, pool);

            contentSource.ClearPrefetch();

            return (buffer, length);
        }

        /// <summary>
        /// Kicks off the one-time scrub-session setup at most once
        /// (_scrubSetupTask, memoized) and returns whatever attempt is
        /// current — see class remarks, SCRUB COALESCING, point 2. Callers
        /// wrap the returned Task in their own cancellable WaitAsync rather
        /// than this method taking a CancellationToken itself: the setup
        /// must always run to completion once started, regardless of which
        /// caller triggered it or whether that caller is later superseded.
        /// </summary>
        private Task EnsureScrubSessionBaseAsync(int width, int height) =>
            _scrubSetupTask ??= BuildScrubSessionAsync(width, height);

        private async Task BuildScrubSessionAsync(int width, int height)
        {
            var nativeSizes = new ConcurrentDictionary<Guid, (int, int)>();
            var decodePlans = new ConcurrentDictionary<Guid, DecodeHwAccelPlan>();

            await PrepareScrubNativeInfoAsync(
                Timeline, RenderSettings.HardwareAccelerator, nativeSizes, decodePlans);

            _scrubNativeSizes = nativeSizes;
            _scrubDecodePlans = decodePlans;
            _scrubContentSource = new ScrubFrameSource(
                RenderSettings.Framerate, RenderSettings.HardwareAccelerator, nativeSizes, decodePlans);

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
                CancellationTokenSource? pending = Interlocked.Exchange(ref _scrubSupersedeCts, null);
                if (pending != null)
                {
                    pending.Cancel();
                    pending.Dispose();
                }

                _scrubSetupTask = null;

                _scrubContentSource?.Dispose();
                _scrubContentSource = null;

                _scrubSurfacePool?.Dispose();
                _scrubSurfacePool = null;

                _scrubGpuContext?.Dispose();
                _scrubGpuContext = null;

                _scrubNativeSizes = null;
                _scrubDecodePlans = null;
            }
            finally
            {
                _scrubGate.Release();
            }
        }

        /// <summary>
        /// Native size + decode-hwaccel-plan probing for scrubbing/reverse
        /// playback — deliberately NOT RenderContentPreparation.
        /// PrepareContentAsync, which also consults OptimizedMediaCache.
        /// Scrubbing/reverse always decode straight from each clip's own
        /// original source now (see class remarks, I-FRAME-ONLY SCRUB/
        /// REVERSE) — this only probes what ScrubFrameSource actually needs
        /// (native dimensions, for aspect-fit math, and a decode plan), with
        /// no cache lookup at all.
        /// </summary>
        private static async Task PrepareScrubNativeInfoAsync(
            Timeline timeline, HardwareAccelerator hwAccel,
            ConcurrentDictionary<Guid, (int, int)> nativeSizes,
            ConcurrentDictionary<Guid, DecodeHwAccelPlan> decodePlans)
        {
            var tasks = new List<Task>();

            foreach (VideoChannel channel in timeline.VideoChannels)
            {
                foreach (Clip clip in channel.Clips)
                {
                    if (clip is not VideoClip video) continue;

                    foreach (VideoSourceNode media in video.Graph.Nodes.OfType<VideoSourceNode>())
                    {
                        if (media.Source.Type != SourceType.Video) continue;
                        tasks.Add(ProbeOneAsync(media));
                    }
                }
            }

            await Task.WhenAll(tasks);

            async Task ProbeOneAsync(VideoSourceNode media)
            {
                (int width, int height) = await MediaProbe.GetDimensionsAsync(media.Source.Path);
                nativeSizes[media.Id] = (width, height);
                decodePlans[media.Id] = await FfmpegRunner.GetDecodePlanAsync(media.Source.Path, hwAccel);
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
        /// REVERSE PLAYBACK (Speed &lt; 0): steps backward through the
        /// timeline at I-FRAME GRANULARITY — reuses the exact same instant,
        /// keyframe-snapped decode ScrubToAsync uses (ScrubFrameSource /
        /// KeyframeIndex / SkSourceDecoder.DecodeSingleFrameAsync), NOT the
        /// persistent forward-only SkSourceDecoder pipe VideoLoopAsync uses,
        /// which structurally cannot move backward at all. See Playback's
        /// class remarks, I-FRAME-ONLY SCRUB/REVERSE.
        ///
        /// Audio never participates here — Play() already gates audio to
        /// Speed == 1 (audioParticipates), which negative Speed never
        /// satisfies — so this loop is always the sole leader of its own
        /// session; there is no reference-clock-follow branch to consider,
        /// unlike VideoLoopAsync.
        ///
        /// PACING mirrors VideoLoopAsync's own leader pacing (a Stopwatch,
        /// content-time-offset-scaled-by-1/|Speed|) just walking frame
        /// indices DOWN instead of up. Every step recomposes a full frame at
        /// its own arbitrary TimeSpan position via ComposeInstantFrameAsync,
        /// passing `token` through so Stop()/Dispose() cancels any in-flight
        /// decode promptly (same cancellation plumbing ScrubToAsync uses —
        /// see class remarks, SCRUB COALESCING).
        ///
        /// VISUAL CHARACTER, NAMED NOT HIDDEN: because only I-frames are
        /// ever decoded, positions that fall within the same GOP as the
        /// last-rendered one land on the SAME cached keyframe image (see
        /// ScrubFrameSource's own remarks) — reverse playback looks like a
        /// fast rewind (jumps between keyframes, typically a few seconds
        /// apart) rather than smooth backward motion. That's the deliberate
        /// trade-off behind "instant, no buffering, no persistent decoder."
        /// </summary>
        private async Task ReverseVideoLoopAsync(
            CancellationToken token, TimeSpan startPosition,
            PlaybackStartGate startGate, PlaybackPauseGate pauseGate,
            PlaybackReferenceClock referenceClock)
        {
            int width = (int)RenderSettings.Resolution.X;
            int height = (int)RenderSettings.Resolution.Y;
            int fps = RenderSettings.Framerate;
            double speedMagnitude = Math.Abs(Speed);

            try
            {
                try
                {
                    var nativeSizes = new ConcurrentDictionary<Guid, (int, int)>();
                    var decodePlans = new ConcurrentDictionary<Guid, DecodeHwAccelPlan>();

                    await PrepareScrubNativeInfoAsync(
                        Timeline, RenderSettings.HardwareAccelerator, nativeSizes, decodePlans);

                    using var contentSource = new ScrubFrameSource(
                        fps, RenderSettings.HardwareAccelerator, nativeSizes, decodePlans);

                    using GpuContext gpuContext = GpuContext.Create(
                        RenderSettings.HardwareAccelerator, RenderSettings.GpuAdapterIndex);
                    using var surfacePool = new SkSurfacePool(
                        gpuContext.GRContext, width, height, Timeline.VideoChannels.Count);

                    int startFrame = (int)(startPosition.TotalSeconds * fps);

                    (byte[] warmupBuffer, int warmupLength) = await ComposeInstantFrameAsync(
                        contentSource, surfacePool, startPosition, width, height, fps, token);
                    EditSharpConfig.Logger.LogVerbose("Reverse video warm-up frame rendered.");

                    await startGate.ReadyAndWaitAsync(token);

                    var clock = Stopwatch.StartNew();
                    EditSharpConfig.Logger.LogVerbose("Reverse video pacing clock started.");

                    referenceClock.Report(startPosition);
                    try
                    {
                        OnVideoFrame(new VideoFrameEventArgs(warmupBuffer, warmupLength, width, height, startPosition));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(warmupBuffer);
                    }

                    if (startFrame <= 0)
                    {
                        TearDownAfterNaturalEnd();
                        OnEndReached(EventArgs.Empty);
                        return;
                    }

                    for (int frameIndex = startFrame - 1; frameIndex >= 0; frameIndex--)
                    {
                        TimeSpan frameOffset = TimeSpan.FromSeconds((startFrame - frameIndex) / (double)fps);
                        TimeSpan framePosition = startPosition - frameOffset;
                        if (framePosition < TimeSpan.Zero) framePosition = TimeSpan.Zero;

                        while (true)
                        {
                            if (token.IsCancellationRequested) return;

                            if (pauseGate.IsPaused)
                            {
                                clock.Stop();
                                try { await pauseGate.WaitIfPausedAsync(token); }
                                catch (OperationCanceledException) { return; }
                                clock.Start();
                                continue;
                            }

                            break;
                        }

                        TimeSpan targetElapsed = TimeSpan.FromSeconds(frameOffset.TotalSeconds / speedMagnitude);
                        TimeSpan actualElapsed = clock.Elapsed;

                        if (targetElapsed > actualElapsed)
                        {
                            try { await Task.Delay(targetElapsed - actualElapsed, token); }
                            catch (OperationCanceledException) { return; }
                        }

                        (byte[] buffer, int length) = await ComposeInstantFrameAsync(
                            contentSource, surfacePool, framePosition, width, height, fps, token);

                        referenceClock.Report(framePosition);

                        try
                        {
                            OnVideoFrame(new VideoFrameEventArgs(buffer, length, width, height, framePosition));
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }

                        if (framePosition == TimeSpan.Zero) break;
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
                    startGate.Fault(ex);
                    throw;
                }
            }
            finally
            {
                lock (_stateLock) { _isPlaying = false; }
            }
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
                    if (!video.Graph.Nodes.OfType<VideoSourceNode>().Any(m => m.Source.Type == SourceType.Video)) continue;
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