using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EditSharp;
using EditSharp.Components;
using EditSharp.Components.Clips;
using EditSharp.Render;

namespace EditSharp.Playback
{
    /// <summary>
    /// Audio-based playback for timelines.
    ///
    /// Built directly on top of Renderer's Skia compositor primitives
    /// (RenderContentPreparation, SkClipContentSource, SkFrameCompositor,
    /// GpuContext, SkSurfacePool) rather than re-deriving them — a live
    /// preview and a full render both start from "what does every clip need
    /// before frame 0," and item 8/11's construction-site lesson was
    /// specifically about what duplicating that kind of logic costs.
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
    /// SYNCHRONIZED STARTUP (PlaybackStartGate + AudioLeadTime): the video
    /// loop and the audio engine each need real setup time before either
    /// can start pacing itself, and that setup is asymmetric (video's is
    /// heavier — see PlaybackStartGate's own remarks). Both loops finish
    /// their own setup, signal the gate, and only THEN start pacing — this
    /// closed an observed video/audio desync that traced back to the two
    /// loops' pacing clocks starting at different real wall-clock moments.
    /// AudioLeadTime, separately, lets audio deliver a burst of backlog to
    /// the consumer BEFORE that synchronized start — see
    /// PlaybackAudioEngine's own remarks for why that's safe and doesn't
    /// reintroduce a content-position offset.
    ///
    /// PLAYBACKMODE / LEADER-FOLLOWER (PlaybackReferenceClock): exactly one
    /// of video/audio is the LEADER for a given session — it paces itself
    /// on its own real Stopwatch, unchanged from before PlaybackMode
    /// existed, and reports its delivered position into a shared
    /// PlaybackReferenceClock. The other stream (if any) is the FOLLOWER —
    /// instead of its own Stopwatch, it polls the reference clock and only
    /// delivers once the leader has actually reached that content position,
    /// catching up with no artificial delay if it falls behind rather than
    /// racing ahead on an independent timeline. The mapping:
    ///   - SyncToAudio: audio leads, video follows.
    ///   - EveryFrame: video leads, audio follows.
    ///   - FrameDropping (or Speed != 1, where audio doesn't participate at
    ///     all): neither follows — both pace independently on their own
    ///     real-time clock, exactly as before this existed. Position, in
    ///     this mode, reflects whichever of the two happened to report last
    ///     — both are independently leaders here, and both report; since
    ///     they're synchronized at startup and each individually accurate,
    ///     this shows up as at most a frame/chunk's worth of jitter in
    ///     Position, not a real desync. Not fixed further — flagged as a
    ///     known, harmless quirk of this mode's design.
    ///
    /// A follower "catching up with no artificial delay" is NOT the same
    /// thing as gap 4's frame-skipping. A follower that's behind still
    /// renders and calls SkFrameCompositor.RenderFrame / decodes via
    /// SkSourceDecoder.NextFrame() for every frame — it just doesn't ALSO
    /// wait between them. True skipping (avoiding that work entirely for
    /// frames that will never be shown) is not possible without a decoder
    /// redesign — see gap 4, which this does not close.
    ///
    /// PAUSE VS STOP (PlaybackPauseGate): genuinely different operations.
    /// Pause() halts both loops in place via a resettable gate they check
    /// every iteration — decoders, the GpuContext/SkSurfacePool, and
    /// PlaybackAudioEngine's ffmpeg process all stay alive, ready to
    /// continue immediately via Play() (no separate Resume() method — see
    /// Play()'s own remarks for why). Stop() tears the whole session down
    /// via cancellation and is for when you're actually done — e.g.
    /// swapping Timeline out from under a session, which Pause()
    /// specifically does NOT support (everything paused stays keyed to the
    /// Timeline that was playing when Pause() was called). Pausing a
    /// LEADER naturally stalls its follower too (the reference clock
    /// simply stops advancing), but the follower checks the pause gate
    /// directly as well, rather than relying on that side effect alone.
    ///
    /// KNOWN GAPS — tracked, not hidden, and re-prioritized per direct
    /// feedback (highest priority first):
    ///   1. SPEED &lt;= 0 (reverse playback) is NOT supported yet, but IS
    ///      considered a real near-term need for an NLE consumer, not a
    ///      nice-to-have. SkSourceDecoder's pipe decode is forward-only by
    ///      contract; reverse playback needs either a buffered scrub window
    ///      or a decoder design that supports it directly. Play() throws
    ///      rather than silently producing wrong output in the meantime.
    ///   2. ARBITRARY SPEED (audio tracking Speed != 1) is a MUST-HAVE, not
    ///      deferred-maybe. v1 only streams audio PCM in real time and
    ///      skips it entirely otherwise (logged, not silent) — needs real
    ///      resampling (and a pitch decision) to close.
    ///   3. Seeking to a nonzero start position may need the audio
    ///      composition itself to carry a seek offset (per-clip atrim, or
    ///      an output-level -ss), not just PlaybackAudioEngine discarding
    ///      leading PCM the way it does today — that's real decode cost
    ///      paid for audio that's never delivered on a deep seek.
    ///   4. TRUE FRAME-SKIPPING (avoiding decode/render work for frames
    ///      that will never be shown, needed for Speed &gt; 1 especially at
    ///      4x+) remains open — see the leader/follower remarks above for
    ///      exactly what's NOT the same thing as this.
    ///   5. PlaybackMode WIRING — CLOSED this pass. SyncToAudio and
    ///      EveryFrame are both real now (see leader/follower remarks
    ///      above). FrameDropping is wired for mode SELECTION but doesn't
    ///      yet do anything FrameDropping-specific beyond what
    ///      leader/follower already provides — it needs gap 4 to become
    ///      meaningfully different from today's default.
    /// </summary>
    public class Playback : IDisposable
    {
        //timeline + settings describing what to play back and how
        public required Timeline Timeline;

        public required RenderSettings RenderSettings;

        //determines which stream leads and which follows — see class
        //remarks for the full SyncToAudio/EveryFrame/FrameDropping mapping
        public PlaybackMode PlaybackMode = PlaybackMode.SyncToAudio;

        //speed at which to play back the timeline
        //NOTE: only positive values are currently supported — see class
        //remarks, gap 1. A negative or zero Speed throws from Play(), not
        //silently clamped. Values != 1 currently play video only — gap 2.
        public float Speed = 1f;

        //how much audio to deliver to the consumer AHEAD of when playback
        //actually starts, so an audio output device (WasapiOut, etc.) can
        //be primed with real backlog before it starts pulling — instead of
        //starting cold. See PlaybackAudioEngine's burst-delivery remarks
        //for the mechanism. Only affects sessions where audio participates
        //(Speed == 1); ignored otherwise. Defaults to zero (no lead, prior
        //behavior unchanged).
        public TimeSpan AudioLeadTime = TimeSpan.Zero;

        //how far along the playback is through the timeline — READ ONLY
        //from outside deliberately, see class remarks. Reads live from the
        //active session's PlaybackReferenceClock while one exists;
        //otherwise reflects wherever the last session left off.
        private TimeSpan _lastKnownPosition = TimeSpan.Zero;
        private PlaybackReferenceClock? _referenceClock;
        public TimeSpan Position => _referenceClock?.Position ?? _lastKnownPosition;

        //publicly accessible check if a session is active (playing OR paused)
        public bool IsPlaying => _isPlaying;

        //publicly accessible check if the active session is currently paused
        public bool IsPaused => _pauseGate?.IsPaused ?? false;

        public event EventHandler<AudioSampleEventArgs>? AudioSample;

        //raised with a new chunk of mixed PCM audio, in real time (Speed == 1 only)
        protected virtual void OnAudioSample(AudioSampleEventArgs e)
        {
            AudioSample?.Invoke(this, e);
        }

        public event EventHandler<VideoFrameEventArgs>? VideoFrame;

        //raised with a new video frame's pixels when it is ready for playback
        protected virtual void OnVideoFrame(VideoFrameEventArgs e)
        {
            VideoFrame?.Invoke(this, e);
        }

        public event EventHandler? EndReached;

        //raised when the end of the timeline is reached
        //(reverse playback / "beginning reached" isn't supported yet — see class remarks)
        protected virtual void OnEndReached(EventArgs e)
        {
            EndReached?.Invoke(this, e);
        }

        public event EventHandler? PlaybackStarted;

        //raised exactly once per session, at the real moment pacing
        //starts (i.e. once setup AND, if audio participates, its lead
        //burst are both done). This is the consumer's cue that NOW is when
        //an audio output device should actually start pulling — e.g. call
        //WasapiOut.Play() from this handler instead of counting samples.
        //May fire from a background thread — marshal to the UI thread
        //yourself if touching UI from a handler.
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

        /// <summary>
        /// Starts, resumes, or seeks-and-plays — one entry point covering
        /// all three, per direct feedback that a separate Resume() method
        /// only added surface area without adding real capability.
        /// Unconditionally leaves playback UNPAUSED regardless of prior
        /// state — that's deliberate, not a side effect:
        ///
        ///   - NOT currently active, startPosition omitted: starts a fresh
        ///     session at wherever Position last was (0 initially).
        ///   - NOT currently active, startPosition given: starts fresh at
        ///     that position.
        ///   - ACTIVE (playing or paused), startPosition omitted: unpauses
        ///     if paused; a harmless no-op if already actively playing
        ///     (Resume() on an unpaused session was already a no-op).
        ///   - ACTIVE (playing or paused), startPosition given: a SEEK.
        ///     Requires a full session restart under the hood regardless
        ///     of entry point — decoders have to reopen at the new
        ///     position (see SkClipContentSource's seekOffsets remarks) —
        ///     so this tears the current session down via Stop() and
        ///     starts a fresh one at startPosition.
        /// </summary>
        public void Play(TimeSpan? startPosition = null)
        {
            lock (_stateLock)
            {
                if (_isPlaying && startPosition == null)
                {
                    _pauseGate?.Resume();
                    return;
                }
            }

            //Deliberately OUTSIDE the lock above: Stop() blocks waiting for
            //the old video loop to finish, and that loop's own teardown
            //needs _stateLock too (see TearDownAfterNaturalEnd) — holding
            //this method's own lock across that wait would deadlock the
            //calling thread against itself. Stop() manages its own locking
            //correctly and no-ops cleanly if there's nothing active.
            if (_isPlaying) Stop();

            lock (_stateLock)
            {
                //Something else (a concurrent caller) may have started a
                //new session in the gap between releasing the lock above
                //and reacquiring it here — bail cleanly rather than
                //stomping on it. Not a scenario this class otherwise
                //guards heavily against multi-threaded callers, but cheap
                //to check here since we're already holding the lock.
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

                //audio v1: real-time only, see class remarks, gap 2
                bool audioParticipates = Math.Abs(Speed - 1f) < 0.0001f;

                //Leader/follower roles per PlaybackMode — see class
                //remarks. Both false means both stream independently, same
                //as before PlaybackMode existed (FrameDropping, or no
                //audio participating at all).
                bool videoFollows = audioParticipates && PlaybackMode == PlaybackMode.SyncToAudio;
                bool audioFollows = audioParticipates && PlaybackMode == PlaybackMode.EveryFrame;

                //One shared gate so video's pacing and (if present) audio's
                //pacing both start at the SAME real moment. "Ready" for
                //audio now means "setup AND lead burst done" — see
                //PlaybackAudioEngine. onReleased fires PlaybackStarted
                //exactly once, right as the gate opens.
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
                            resolvedStart, AudioLeadTime, startGate, pauseGate,
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

        /// <summary>
        /// Halts playback IN PLACE — decoders, the GPU context/surface
        /// pool, and PlaybackAudioEngine's ffmpeg process all stay open,
        /// ready to continue immediately via Play(). Not the same
        /// operation as Stop() — see class remarks. No-op if not currently
        /// playing.
        /// </summary>
        public void Pause()
        {
            lock (_stateLock)
            {
                if (!_isPlaying || _pauseGate == null) return;
                _pauseGate.Pause();
            }

            EditSharpConfig.Logger.LogVerbose("Playback paused.");
        }

        /// <summary>
        /// Fully tears the session down — cancels both loops, disposes the
        /// audio engine (which kills its ffmpeg process), and lets
        /// VideoLoopAsync's own finally block dispose its decoders/GPU
        /// context/surface pool. Use this when actually done with the
        /// session, e.g. before swapping Timeline out for a different one.
        /// For a temporary halt you intend to continue from, use Pause()
        /// instead — it's meaningfully cheaper (no decoder/process
        /// teardown-and-reopen) and that's the whole reason it exists.
        /// </summary>
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

                //Capture the final position before dropping the reference
                //clock, so Position stays meaningful afterward (and so
                //Play()'s own "resume from wherever we stopped" default
                //still works).
                _lastKnownPosition = _referenceClock?.Position ?? _lastKnownPosition;
                _referenceClock = null;
            }

            //Cancelling unblocks a paused loop too — WaitIfPausedAsync
            //awaits the SAME token, so Stop() while paused doesn't need any
            //special-casing to also call Resume() first.
            cts?.Cancel();

            try { _videoTask?.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { /* expected */ }

            _audioEngine?.Dispose();
            _audioEngine = null;

            cts?.Dispose();
            _videoTask = null;
        }

        private async Task VideoLoopAsync(
            CancellationToken token, TimeSpan startPosition,
            PlaybackStartGate startGate, PlaybackPauseGate pauseGate,
            PlaybackReferenceClock referenceClock, bool followsReferenceClock)
        {
            int width = (int)RenderSettings.Resolution.X;
            int height = (int)RenderSettings.Resolution.Y;
            int fps = RenderSettings.Framerate;

            var tempFiles = new ConcurrentBag<string>();
            var nativeSizes = new ConcurrentDictionary<Clip, (int, int)>();
            var staticImagePaths = new ConcurrentDictionary<Clip, string>();
            var decodePlans = new ConcurrentDictionary<Clip, DecodeHwAccelPlan>();

            try
            {
                await RenderContentPreparation.PrepareContentAsync(
                    Timeline, width, height, RenderSettings.HardwareAccelerator,
                    nativeSizes, staticImagePaths, decodePlans, tempFiles);

                Dictionary<Clip, TimeSpan> seekOffsets = ComputeSeekOffsets(Timeline, startPosition);

                Dictionary<int, List<Clip>> decoderReleaseSchedule =
                    RenderContentPreparation.BuildDecoderReleaseSchedule(Timeline, fps);

                using var contentSource = new SkClipContentSource(
                    fps, nativeSizes, staticImagePaths, decodePlans, seekOffsets);

                using GpuContext gpuContext = GpuContext.Create(RenderSettings.HardwareAccelerator);
                using var surfacePool = new SkSurfacePool(
                    gpuContext.GRContext, width, height, Timeline.Channels.Count);

                int startFrame = (int)(startPosition.TotalSeconds * fps);
                int totalFrames = Math.Max(1, (int)Math.Ceiling(Timeline.Duration.TotalSeconds * fps));

                //WARM-UP: render the actual first frame NOW, before
                //signaling ready — this is what pays the one-time GPU
                //pipeline-state / hardware decoder session cost (observed:
                //multiple seconds for a session's first rendered frame, a
                //few ms for every frame after). Paying it here, during
                //setup, means it's absorbed before PlaybackStarted fires
                //rather than showing up as a multi-second stall right after
                //the consumer's been told "now." Happens regardless of
                //leader/follower role — GPU/decoder priming is orthogonal
                //to which stream paces which.
                //
                //This CANNOT be a separate, discarded test frame —
                //SkSourceDecoder.NextFrame() is strictly sequential and
                //one-shot per its own contract; decoding a throwaway frame
                //0 and discarding it would leave the NEXT NextFrame() call
                //returning decoder frame 1's content for what's supposed to
                //be frame 0, a real off-by-one correctness bug. So this
                //render below IS the actual first frame — its bytes are
                //held and delivered as-is once the gate opens, not
                //re-rendered.
                //
                //KNOWN GAP: this only warms up whatever's visible AT
                //startFrame. A clip that first becomes visible later in the
                //timeline still pays its own decoder-open cold-start cost
                //the first time ITS decoder opens, mid-session — a smaller,
                //separate hitch this doesn't address. Not fixed here.
                FrameState warmupState = FrameStateResolver.Resolve(Timeline, startFrame, fps, nativeSizes);
                (byte[] warmupBuffer, int warmupLength) = SkFrameCompositor.RenderFrame(
                    warmupState, contentSource, width, height, fps, surfacePool);
                EditSharpConfig.Logger.LogVerbose("Video warm-up frame rendered.");

                //Setup (including warm-up) is done — wait for audio (if
                //any) to also be ready before pacing begins. Nothing
                //between this line and delivering the warm-up frame should
                //do real work; that gap is exactly what this gate exists
                //to eliminate.
                await startGate.ReadyAndWaitAsync(token);

                //LEADER-ONLY: own Stopwatch. A FOLLOWER has no local clock
                //at all — it paces entirely against referenceClock.Position,
                //written by whichever stream IS leader this session.
                Stopwatch? clock = followsReferenceClock ? null : Stopwatch.StartNew();
                EditSharpConfig.Logger.LogVerbose(followsReferenceClock
                    ? "Video now following the reference clock."
                    : "Video pacing clock started.");

                //Deliver the already-rendered warm-up frame immediately —
                //targetElapsed for startFrame is 0, and there's no render
                //cost left to pay, so this goes out with no delay.
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

                    //Wait for both "not paused" and (if following) "the
                    //leader has actually reached this frame's due time,"
                    //re-checking both after each wait since either could
                    //change while waiting on the other. If following and
                    //ALREADY past due (leader's ahead of us), this falls
                    //straight through with no wait — the catch-up-without-
                    //delay behavior described in class remarks.
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

                        if (followsReferenceClock && referenceClock.Position < framePosition)
                        {
                            try { await Task.Delay(PlaybackReferenceClock.PollInterval, token); }
                            catch (OperationCanceledException) { return; }
                            continue;
                        }

                        break;
                    }

                    FrameState state = FrameStateResolver.Resolve(Timeline, frameIndex, fps, nativeSizes);

                    (byte[] buffer, int length) = SkFrameCompositor.RenderFrame(
                        state, contentSource, width, height, fps, surfacePool);

                    if (!followsReferenceClock)
                    {
                        //LEADER: pace against our own Stopwatch, scaled by
                        //Speed — unchanged from before PlaybackMode existed.
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

                //Tear down the FULL session state — same as Stop() would —
                //BEFORE firing OnEndReached, not after. OnEndReached's
                //invocation is synchronous, and a consumer's handler may
                //well call Play() directly from it (looping/replay is the
                //obvious case). If state were still reset AFTER firing the
                //event, a Play() call made from inside that handler would
                //see _isPlaying still true (this method hasn't returned
                //yet) and silently no-op — exactly the bug this closes.
                //Also fixes a real resource leak: natural completion
                //previously never disposed _audioEngine (dangling Process
                //handle, undeleted temp files) the way Stop() always did.
                TearDownAfterNaturalEnd();

                OnEndReached(EventArgs.Empty);
            }
            finally
            {
                foreach (string path in tempFiles)
                {
                    try { File.Delete(path); } catch { /* best-effort cleanup */ }
                }

                //Redundant-but-harmless safety net for the CANCELLED exit
                //paths (Stop() already sets this itself, before this
                //method even observes cancellation) — NOT relied on for
                //the natural-end path, which is handled explicitly above,
                //specifically so it can run before OnEndReached fires.
                lock (_stateLock) { _isPlaying = false; }
            }
        }

        /// <summary>
        /// Full session teardown for the NATURAL-END case specifically —
        /// same fields Stop() clears, but callable from within the video
        /// loop itself (no CancellationTokenSource to cancel or Task to
        /// block on, since we ARE that task and it's already finished
        /// running). See the call site's own remarks for why this has to
        /// happen before OnEndReached fires, not after.
        /// </summary>
        private void TearDownAfterNaturalEnd()
        {
            CancellationTokenSource? cts;

            lock (_stateLock)
            {
                if (!_isPlaying) return; // already torn down by a concurrent Stop()

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
        /// For every video SourceClip already visible at `position`, the
        /// additional offset (beyond the clip's own trim start) its decoder
        /// needs to open at — see SkClipContentSource's seekOffsets remarks
        /// for the full reasoning. Clips that haven't started yet at
        /// `position` need no entry; their decoders open normally, at zero
        /// extra offset, whenever the loop first reaches them.
        /// </summary>
        private static Dictionary<Clip, TimeSpan> ComputeSeekOffsets(Timeline timeline, TimeSpan position)
        {
            var offsets = new Dictionary<Clip, TimeSpan>();

            foreach (Channel channel in timeline.Channels)
            {
                foreach (Clip clip in channel.Clips.Values)
                {
                    if (clip is not SourceClip { Source.Type: SourceType.Video }) continue;
                    if (position < clip.Start || position >= clip.End) continue;

                    offsets[clip] = position - clip.Start;
                }
            }

            return offsets;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
