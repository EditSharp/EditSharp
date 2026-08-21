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
    /// SYNCHRONIZED STARTUP (PlaybackStartGate): the video loop and the
    /// audio engine each need real setup time before either can start
    /// pacing itself, and that setup is asymmetric (video's is heavier —
    /// see PlaybackStartGate's own remarks). Both loops finish their own
    /// setup, signal the gate, and only THEN start pacing — this closed an
    /// observed video/audio desync that traced back to the two loops'
    /// pacing clocks starting at different real wall-clock moments.
    ///
    /// AUDIO DELIVERY IS "PLAY THIS NOW," DELIBERATELY NOT PRE-BUFFERED —
    /// an earlier version of this had an AudioLeadTime field that let
    /// audio deliver a burst of backlog to the consumer ahead of the
    /// synchronized start, so an output device could be primed before
    /// pulling. Removed. Two reasons: (1) the underrun problem it was
    /// built to solve turned out to be entirely a buffer-corruption bug
    /// elsewhere (fixed separately, confirmed by testing with ZERO
    /// pre-buffering afterward), not a genuine backlog need; (2) it's
    /// fundamentally incompatible with EveryFrame mode. A real output
    /// device drains its buffer on its OWN free-running clock once
    /// started — pre-buffering ahead of time controls how much backlog
    /// exists, but can't make audio WAIT to become audible in sync with an
    /// irregular video renderer, because the device isn't listening to our
    /// pacing decisions once it's running. Delivering exactly when due,
    /// with no lookahead, means delivery timing directly IS audible
    /// timing — which is what following a leader (or being one) actually
    /// requires.
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
    /// OPTIMIZED-MEDIA CACHE (OptimizedMediaCache, EditSharp.Render): a
    /// video clip's decoder here may open against a persistent, content-
    /// addressed DNxHR/ProRes proxy instead of the clip's true original
    /// source file, whenever RenderContentPreparation.ProbeVideoAsync finds
    /// one already built and big enough — see that method's own remarks.
    /// This is what makes a SEEK (see Play(startPosition), gap 3, and
    /// ComputeSeekOffsets below) cheap even against source codecs that are
    /// otherwise expensive to seek into: DNxHR/ProRes are all-intra, so
    /// opening a decoder at an arbitrary offset costs the same as opening
    /// one at zero. Nothing here TRIGGERS a build on a cache miss —
    /// Playback only ever reads whatever a consumer app has already warmed
    /// via OptimizedMediaCache.PrewarmAsync (typically called at import
    /// time), so an uncached source plays back exactly as it always has,
    /// with no behavior change and no build competing with this session's
    /// own decode for CPU.
    ///
    /// SCRUBBING (SupportsScrubbing / RefreshScrubbingSupportAsync /
    /// ScrubToAsync / EndScrubbing): a second, separate playback surface
    /// for "render me one frame at this arbitrary position, right now,"
    /// distinct from Play(startPosition)'s "start a real paced session
    /// there." Only meaningfully cheap when EVERY video source involved is
    /// already on all-intra optimized media — a source still on its
    /// original, likely long-GOP, delivery codec can be expensive to open
    /// at an arbitrary offset, which is exactly what scrubbing does
    /// repeatedly. SupportsScrubbing exposes that precondition as a single
    /// read-only bool rather than making every consumer re-derive it
    /// per-clip; it's computed via
    /// RenderContentPreparation.AllVideoSourcesHaveSufficientCachedMediaAsync,
    /// the same cache-sufficiency logic ProbeVideoAsync uses, so it can
    /// never disagree with what a real session would actually do.
    ///
    /// FORWARD-STEPPING, NOT REOPEN-PER-TICK — REWORKED AFTER DIRECT
    /// MEASUREMENT that a fresh decoder open, even against +faststart
    /// optimized media with reduced probing (see SkSourceDecoder's
    /// fastOpen), still costs on the order of ~0.1-0.3s — a real, mostly
    /// fixed cost (process spawn, codec/filter init) that a per-tick
    /// reopen design pays on EVERY scrub call, defeating "arbitrary seek
    /// is as cheap as sequential decode" for anything that scrubs more
    /// than once. The actual cheap operation all-intra media buys is
    /// SEQUENTIAL FORWARD decode from an already-open decoder — that's the
    /// property this class now spends: a scrub session keeps ONE
    /// persistent SkClipContentSource alive (see _scrubContentSource)
    /// across calls, and ScrubToAsync steps it FORWARD frame-by-frame
    /// (discarding intermediate frames, keeping only the target) rather
    /// than reopening, for any forward move within
    /// ScrubForwardStepBudgetFrames of the last delivered position. Only a
    /// genuine jump — backward, or a large forward jump — pays the reopen
    /// cost, and pays it exactly once for that jump, not once per
    /// subsequent frame. A scrub bar being dragged (the overwhelmingly
    /// common real interaction) is a long run of small forward steps, so
    /// this is the case that actually matters.
    ///
    /// A scrub session otherwise reuses expensive one-time setup
    /// (GpuContext, SkSurfacePool, the content-prep dictionaries from one
    /// PrepareContentAsync call) across every ScrubToAsync call until
    /// EndScrubbing/Dispose — see EnsureScrubSessionBaseAsync.
    ///
    /// KNOWN GAPS — tracked, not hidden, and re-prioritized per direct
    /// feedback (highest priority first):
    ///   1. SPEED &lt;= 0 (reverse playback) is NOT supported yet, but IS
    ///      considered a real near-term need for an NLE consumer, not a
    ///      nice-to-have. SkSourceDecoder's pipe decode is forward-only by
    ///      contract; reverse playback needs either a buffered scrub window
    ///      or a decoder design that supports it directly. Play() throws
    ///      rather than silently producing wrong output in the meantime.
    ///      NOTE: ScrubToAsync's forward-stepping does NOT help here — a
    ///      backward scrub still pays a full reopen, same as before.
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
    ///   6. SCRUBBING — CLOSED this pass for the all-optimized-media,
    ///      forward-or-small-jump case (see the SCRUBBING/FORWARD-STEPPING
    ///      remarks above). A timeline with even one video source not yet
    ///      cached simply reports SupportsScrubbing == false. A backward
    ///      scrub or a jump larger than ScrubForwardStepBudgetFrames still
    ///      pays a full reopen — genuinely can't be avoided without a
    ///      buffered/multi-decoder design this pass doesn't build.
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

        //READ ONLY — whether every video source in Timeline currently has
        //enough cached optimized media for ScrubToAsync to be uniformly
        //cheap. See the class remarks' SCRUBBING section for the full
        //reasoning. Kept up to date automatically at the start of every
        //Play() session; call RefreshScrubbingSupportAsync yourself to
        //check (or re-check, e.g. after a background prewarm completes)
        //before ever calling Play() or ScrubToAsync.
        public bool SupportsScrubbing { get; private set; }

        public event EventHandler<AudioSampleEventArgs>? AudioSample;

        //raised with a new chunk of mixed PCM audio, in real time (Speed == 1 only)
        protected virtual void OnAudioSample(AudioSampleEventArgs e)
        {
            AudioSample?.Invoke(this, e);
        }

        public event EventHandler<VideoFrameEventArgs>? VideoFrame;

        //raised with a new video frame's pixels when it is ready for playback
        //(also raised by ScrubToAsync — see its own remarks)
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

        //How many frames a forward ScrubToAsync step is willing to walk
        //through sequentially (decoding and discarding each intermediate
        //one) before it's cheaper to just pay a fresh reopen's cold-start
        //cost instead — see the class remarks' FORWARD-STEPPING section.
        //Chosen by reasoning about the numbers actually measured (a cold
        //reopen against optimized media costs on the order of ~0.1-0.3s
        //even after +faststart/fastOpen — see SkSourceDecoder's own
        //remarks), not tuned against a real benchmark of this exact
        //stepping path — flagged the same way this codebase flags its own
        //other reasoned-not-measured constants (see MediaHasher.
        //SampleCount). Deliberately small: per-frame decode that's cheap
        //in STEADY STATE can still add up past a cold reopen's fixed cost
        //if stepped too far, and a scrub bar dragged quickly is exactly
        //the case that would hit that ceiling first.
        private const int ScrubForwardStepBudgetFrames = 15;

        //SCRUB SESSION STATE — lazily created by EnsureScrubSessionBaseAsync,
        //reused across many ScrubToAsync calls, torn down by EndScrubbing/
        //Dispose. Deliberately entirely separate from the Play()/_isPlaying
        //session state above: scrubbing is meant to be usable WHILE paused
        //(indeed only while not actively playing — see ScrubToAsync), and
        //keying it off the same fields Play() uses would mean every scrub
        //tick fights Play()'s own state machine for no reason.
        private readonly SemaphoreSlim _scrubGate = new(1, 1);
        private GpuContext? _scrubGpuContext;
        private SkSurfacePool? _scrubSurfacePool;
        private ConcurrentDictionary<Clip, (int, int)>? _scrubNativeSizes;
        private ConcurrentDictionary<Clip, string>? _scrubStaticImagePaths;
        private ConcurrentDictionary<Clip, DecodeHwAccelPlan>? _scrubDecodePlans;
        private ConcurrentDictionary<Clip, string>? _scrubDecodeSourcePaths;
        private ConcurrentBag<string>? _scrubTempFiles;
        private Dictionary<int, List<Clip>>? _scrubDecoderReleaseSchedule;

        //PERSISTENT DECODER STATE — see the class remarks' FORWARD-STEPPING
        //section for why this exists. _scrubContentSource stays open and
        //positioned across calls; _scrubLastFrameIndex is the frame index
        //its decoders currently sit AT (i.e. the last frame actually
        //decoded and delivered); _scrubLastDeliveredBuffer/Length is a
        //standalone COPY (not the pooled render buffer, which gets
        //returned to ArrayPool immediately after delivery) so a repeat
        //ScrubToAsync call for the exact same position can redeliver
        //without touching any decoder — SkSourceDecoder.NextFrame() is
        //one-shot per call, so re-requesting the current frame would
        //otherwise silently decode past it.
        private SkClipContentSource? _scrubContentSource;
        private int? _scrubLastFrameIndex;
        private byte[]? _scrubLastDeliveredBuffer;
        private int _scrubLastDeliveredLength;

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
                    //Un-freezes PlaybackReferenceClock's extrapolation —
                    //see its own remarks on why it needs to be explicitly
                    //paused/resumed, not just left to the pause gate alone.
                    _referenceClock?.ResumeWallClock();
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
                //See PlaybackReferenceClock's remarks — without this,
                //its extrapolation would keep advancing Position through
                //the whole pause window and jump forward incorrectly the
                //instant it resumes.
                _referenceClock?.PauseWallClock();
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

        /// <summary>
        /// Checks (or re-checks) SupportsScrubbing WITHOUT requiring an
        /// active or prior Play() session — e.g. right after import, or
        /// after a consumer's own background OptimizedMediaCache.PrewarmAsync
        /// calls finish. Safe to call at any time, including while playing.
        /// Play() also recomputes this itself at the start of every
        /// session, so calling this beforehand is purely so a consumer can
        /// decide whether to offer scrubbing UI before ever pressing play.
        /// </summary>
        public async Task<bool> RefreshScrubbingSupportAsync(CancellationToken ct = default)
        {
            bool supported = await RenderContentPreparation.AllVideoSourcesHaveSufficientCachedMediaAsync(
                Timeline, (int)RenderSettings.Resolution.X, (int)RenderSettings.Resolution.Y);

            ct.ThrowIfCancellationRequested();

            SupportsScrubbing = supported;
            return supported;
        }

        /// <summary>
        /// Renders and delivers exactly one frame at `position`, via the
        /// existing VideoFrame event — the scrubbing entry point. See the
        /// class remarks' SCRUBBING/FORWARD-STEPPING sections for the full
        /// design.
        ///
        /// Requires SupportsScrubbing (throws InvalidOperationException
        /// otherwise — call RefreshScrubbingSupportAsync first if unsure,
        /// or prewarm the missing sources). Requires that a session isn't
        /// actively PLAYING right now — Pause() first, or never Play() at
        /// all this session — since an actively-pacing video loop and a
        /// scrub tick would otherwise both try to deliver frames/manage
        /// _lastKnownPosition at the same time with no coordination between
        /// them. Scrubbing while genuinely paused is fine and expected —
        /// that's the primary intended use (drag a scrub bar while
        /// paused, then Play() again from wherever the user let go).
        ///
        /// COST MODEL, so a consumer can reason about it: a REPEAT call at
        /// the exact same position is free (redelivers a cached frame, no
        /// decode). A call within ScrubForwardStepBudgetFrames FORWARD of
        /// the last delivered position steps the already-open decoder(s)
        /// forward — cheap, steady-state per-frame decode cost, no process
        /// spawn. A call BACKWARD of the last position, or a forward jump
        /// larger than that budget, pays a full decoder reopen — the same
        /// ~0.1-0.3s cold-start cost Play() itself pays, just for this one
        /// clip's decoder(s) rather than a whole session's.
        /// </summary>
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
                    //Exact repeat of the last delivered position —
                    //SkSourceDecoder.NextFrame() is one-shot-per-call, so
                    //calling it again here would silently advance PAST
                    //this frame rather than re-returning it. Redeliver the
                    //cached bytes instead of touching any decoder.
                    OnVideoFrame(new VideoFrameEventArgs(
                        _scrubLastDeliveredBuffer, _scrubLastDeliveredLength, width, height, position));
                    return;
                }

                //See the class remarks' FORWARD-STEPPING section: only a
                //genuine jump pays a reopen. _scrubContentSource == null
                //covers both "never scrubbed yet this session" and
                //"just torn down by EndScrubbing".
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
                        fps, _scrubNativeSizes!, _scrubStaticImagePaths!, _scrubDecodePlans!,
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
                    FrameState state = FrameStateResolver.Resolve(Timeline, frameIndex, fps, _scrubNativeSizes!);

                    (byte[] buffer, int bufLength) = SkFrameCompositor.RenderFrame(
                        state, contentSource, width, height, fps, _scrubSurfacePool!);

                    if (frameIndex == targetFrameIndex)
                    {
                        pooledBuffer = buffer;
                        length = bufLength;
                    }
                    else
                    {
                        //an intermediate, discarded step — the decode is
                        //what advances the decoder correctly for the NEXT
                        //call; the composited pixels themselves are not
                        //needed
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
                    //Own copy — pooledBuffer is returned to ArrayPool right
                    //after delivery below and must not be relied on to
                    //still hold this frame's bytes afterward.
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

        /// <summary>
        /// One-time (per scrub session) setup shared across every
        /// ScrubToAsync call: GpuContext, SkSurfacePool, the content-prep
        /// dictionaries from a single PrepareContentAsync call, and the
        /// decoder-release schedule. Deliberately does NOT open
        /// _scrubContentSource itself — that depends on the target
        /// position's own seekOffsets, which ScrubToAsync computes only
        /// when a reopen is actually needed (see its own remarks). No-ops
        /// if already prepared. NOT guarded by its own lock — callers hold
        /// _scrubGate for the whole ScrubToAsync call already.
        /// </summary>
        private async Task EnsureScrubSessionBaseAsync(int width, int height, CancellationToken ct)
        {
            if (_scrubGpuContext != null) return;

            var tempFiles = new ConcurrentBag<string>();
            var nativeSizes = new ConcurrentDictionary<Clip, (int, int)>();
            var staticImagePaths = new ConcurrentDictionary<Clip, string>();
            var decodePlans = new ConcurrentDictionary<Clip, DecodeHwAccelPlan>();
            var decodeSourcePaths = new ConcurrentDictionary<Clip, string>();

            await RenderContentPreparation.PrepareContentAsync(
                Timeline, width, height, RenderSettings.HardwareAccelerator,
                nativeSizes, staticImagePaths, decodePlans, decodeSourcePaths, tempFiles);

            ct.ThrowIfCancellationRequested();

            _scrubTempFiles = tempFiles;
            _scrubNativeSizes = nativeSizes;
            _scrubStaticImagePaths = staticImagePaths;
            _scrubDecodePlans = decodePlans;
            _scrubDecodeSourcePaths = decodeSourcePaths;
            _scrubDecoderReleaseSchedule =
                RenderContentPreparation.BuildDecoderReleaseSchedule(Timeline, RenderSettings.Framerate);

            _scrubGpuContext = GpuContext.Create(RenderSettings.HardwareAccelerator);
            _scrubSurfacePool = new SkSurfacePool(
                _scrubGpuContext.GRContext, width, height, Timeline.Channels.Count);
        }

        /// <summary>
        /// Tears down the persistent scrub session (the persistent
        /// SkClipContentSource and its decoders, GpuContext, SkSurfacePool,
        /// content-prep state, and any temp files PrepareContentAsync
        /// created for it), if one was ever started. Safe to call even if
        /// ScrubToAsync was never used. Also called from Dispose() — a
        /// scrub session isn't cleaned up by Stop(), since the two are
        /// deliberately independent (see the SCRUB SESSION STATE fields'
        /// own remarks).
        /// </summary>
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
                _scrubStaticImagePaths = null;
                _scrubDecodePlans = null;
                _scrubDecodeSourcePaths = null;
                _scrubDecoderReleaseSchedule = null;

                if (_scrubTempFiles != null)
                {
                    foreach (string path in _scrubTempFiles)
                    {
                        try { File.Delete(path); } catch { /* best-effort cleanup */ }
                    }

                    _scrubTempFiles = null;
                }
            }
            finally
            {
                _scrubGate.Release();
            }
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

            //which file each video clip's decoder actually opens — see this
            //class's own remarks on OptimizedMediaCache, and
            //RenderContentPreparation.ProbeVideoAsync for how this gets
            //populated. Purely opportunistic: an entry here only ever
            //appears when a persistent cache hit was found and was big
            //enough for this clip; otherwise SkClipContentSource falls back
            //to the clip's own Source.Path exactly as it always has.
            var decodeSourcePaths = new ConcurrentDictionary<Clip, string>();

            try
            {
                await RenderContentPreparation.PrepareContentAsync(
                    Timeline, width, height, RenderSettings.HardwareAccelerator,
                    nativeSizes, staticImagePaths, decodePlans, decodeSourcePaths, tempFiles);

                //SupportsScrubbing is recomputed for every session from the
                //exact same decision PrepareContentAsync/ProbeVideoAsync
                //just made — reusing decodeSourcePaths here (rather than a
                //second cache lookup pass) both saves the extra probing
                //work and guarantees this can never disagree with what THIS
                //session actually opened its decoders against.
                SupportsScrubbing = AllVideoClipsRedirectedToCache(Timeline, decodeSourcePaths);

                Dictionary<Clip, TimeSpan> seekOffsets = ComputeSeekOffsets(Timeline, startPosition);

                Dictionary<int, List<Clip>> decoderReleaseSchedule =
                    RenderContentPreparation.BuildDecoderReleaseSchedule(Timeline, fps);

                using var contentSource = new SkClipContentSource(
                    fps, nativeSizes, staticImagePaths, decodePlans, seekOffsets, decodeSourcePaths);

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

                        if (followsReferenceClock)
                        {
                            TimeSpan gap = framePosition - referenceClock.Position;

                            if (gap > TimeSpan.Zero)
                            {
                                //Compute the ACTUAL expected wait rather
                                //than polling in small fixed increments —
                                //see PlaybackReferenceClock's own remarks
                                //on why this, not a shorter PollInterval,
                                //is the real fix for follower jitter.
                                //Clamped to at least PollInterval so a
                                //wildly-off estimate (leader just paused,
                                //e.g.) still gets re-checked promptly
                                //rather than sleeping through it.
                                TimeSpan wait = gap > PlaybackReferenceClock.PollInterval
                                    ? gap : PlaybackReferenceClock.PollInterval;

                                try { await Task.Delay(wait, token); }
                                catch (OperationCanceledException) { return; }
                                continue; // re-check — the estimate could've been off
                            }
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
        /// Cheap, no-I/O check: true when every video SourceClip in
        /// `timeline` was redirected in `decodeSourcePaths` (i.e. its
        /// decode path differs from its own original Source.Path) —
        /// meaning THIS session's own PrepareContentAsync call actually
        /// found and used a sufficient cache entry for every one of them.
        /// A timeline with no video clips is trivially true. Kept separate
        /// from RenderContentPreparation.AllVideoSourcesHaveSufficientCachedMediaAsync
        /// (which re-probes/re-looks-up from scratch, for use BEFORE a
        /// session exists) purely as a cheap same-session shortcut — the
        /// two must always agree since they're deciding the same thing.
        /// </summary>
        private static bool AllVideoClipsRedirectedToCache(
            Timeline timeline, IReadOnlyDictionary<Clip, string> decodeSourcePaths)
        {
            foreach (Channel channel in timeline.Channels)
            {
                foreach (Clip clip in channel.Clips.Values)
                {
                    if (clip is not SourceClip { Source.Type: SourceType.Video } video) continue;

                    if (!decodeSourcePaths.TryGetValue(clip, out string? decodePath)) return false;
                    if (decodePath == video.Source.Path) return false;
                }
            }

            return true;
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
            EndScrubbing();
        }
    }
}