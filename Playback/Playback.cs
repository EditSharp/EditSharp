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
    /// see the property itself. ScrubToAsync also writes into the SAME
    /// PlaybackReferenceClock when a session exists (paused included), not
    /// only the locally-held fallback — see SCRUBBING WHILE PAUSED UPDATES
    /// Position below. A scrub taken while paused is no longer just a
    /// preview, either — see SCRUB DURING PAUSE FORCES A REAL SEEK ON RESUME
    /// below for how a subsequent Play() actually resumes from it now.
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
    /// PAUSE VS STOP (PlaybackPauseGate): genuinely different operations. A
    /// paused session's GpuContext/SkSurfacePool/decoders stay fully alive
    /// (see EAGER RELEASE ON PAUSE, REVERTED below) — pausing is meant to
    /// be, and is, an essentially free, instantly-resumable operation
    /// UNLESS a scrub actually moved the position during that pause — see
    /// SCRUB DURING PAUSE FORCES A REAL SEEK ON RESUME below.
    ///
    /// SCRUB/REVERSE VIA RAW SCRUB PROXIES (ScrubProxyCache /
    /// ScrubProxyReader / ScrubFrameSource) — REWRITTEN IN CONVERSATION,
    /// REPLACING AN EARLIER ffmpeg-KEYFRAME-DECODE APPROACH ENTIRELY, after
    /// two rounds of real-world testing on real hardware showed that
    /// spawning ANY ffmpeg process per scrub tick — GPU-hwaccel or forced
    /// software — could not be made both fast and crash-safe under a fast
    /// scrub drag (see the two superseded rounds summarized below). Both
    /// ScrubToAsync and reverse playback (Speed &lt; 0) share ONE mechanism,
    /// deliberately — both need "show me approximately this arbitrary
    /// position, instantly, with zero per-tick decode." Every video source
    /// referenced by the Timeline gets a small, pre-built, RAW, decoder-less
    /// scrub proxy — a fixed-size-frame file a scrub/reverse tick reads
    /// directly (one positioned file read, no subprocess, no decode at all —
    /// see ScrubProxyFormat's own remarks for the file shape and why). The
    /// trade-off, named not hidden: what's delivered during scrub/reverse is
    /// accurate to the proxy's own fixed, LOW resolution and fixed SAMPLE
    /// RATE (EditSharpConfig.ScrubProxyTargetShortSide/ScrubProxySampleRate),
    /// not the clip's real decode resolution or the exact requested frame —
    /// normal for a scrub/rewind preview, wrong for a final render (this
    /// mechanism is never used by Render/* or by VideoLoopAsync's normal
    /// forward playback). For reverse playback specifically this means
    /// visual character is a smooth, evenly-paced backward step every
    /// 1/ScrubProxySampleRate seconds of content — a real improvement over
    /// the earlier keyframe-snapped "fast rewind" look, since a proxy's
    /// frame density is no longer tied to the source's own (often several-
    /// seconds-apart) keyframe spacing at all.
    ///
    /// WHY NO ffmpeg PROCESS EVER RUNS DURING A SCRUB/REVERSE TICK, AND WHY
    /// THAT MATTERS BEYOND SPEED: it also means the persistent GPU decoder
    /// backing REAL forward playback (SkSourceDecoder.Start/NextFrame, and
    /// its GpuContext) is never touched, paused, or contended for while a
    /// scrub/reverse session is active — it can stay warm and simply resume
    /// smoothly the moment scrubbing ends, since nothing about scrubbing
    /// ever shared a process, a decode session, or a GPU context with it in
    /// the first place. Scrubbing NEVER touches ffmpeg or any file ffmpeg
    /// has open — it only ever reads pre-built .esrp proxy files via
    /// ScrubProxyReader (System.IO.RandomAccess) — see SCRUB/REVERSE VIA RAW
    /// SCRUB PROXIES above; this remains true after every fix below.
    ///
    /// TWO SUPERSEDED ROUNDS OF FIXES, KEPT HERE AS HISTORY SINCE THE
    /// LESSON EACH ONE TAUGHT SHAPED THIS DESIGN — the mechanism itself (an
    /// ffmpeg one-shot decode per tick) is gone, but the request-coalescing
    /// fix from round 1 is NOT superseded and is still exactly how
    /// ScrubToAsync behaves (see SCRUB COALESCING below):
    ///   ROUND 1 (fixed, real bug): a fast scrub drag firing many
    ///   ScrubToAsync calls used to serialize them FIFO — every call ran a
    ///   real ffmpeg decode to completion for a frame nobody wanted by the
    ///   time it finished. Worse, the one-time per-session setup used to be
    ///   gated by each call's own cancellation token, so a request
    ///   superseded mid-setup killed setup itself and the next request
    ///   restarted it from scratch — under fast enough scrubbing setup could
    ///   never finish at all. Fixed with "latest request wins" coalescing
    ///   plus memoized setup — see SCRUB COALESCING below, which still
    ///   applies unchanged to this rewrite.
    ///   ROUND 2 (found after round 1, itself now superseded by this
    ///   rewrite, not by a further tweak of the same mechanism): even with
    ///   coalescing fixed, each SURVIVING scrub/reverse tick was still slow
    ///   ("wait a few seconds") and a fast enough drag could still lock the
    ///   whole process up irrecoverably. Root cause was GPU-hwaccel context/
    ///   session overhead paid per one-shot tick (and a real risk of
    ///   exhausting the GPU's own concurrent decode-session budget under
    ///   rapid concurrent one-shot GPU decodes — a driver-level hang outside
    ///   any cancellation/process-kill this process could reach). The fix
    ///   tried was forcing SOFTWARE decode for every tick instead — which,
    ///   per direct real-hardware feedback, was NOT the right fix: CPU
    ///   decode of the clip's real native resolution is orders of magnitude
    ///   slower than GPU decode (established earlier, during this project's
    ///   own GPU migration work), so forcing it per-tick just traded one
    ///   flavor of slow/unstable for another (this time crashing outright
    ///   under load, rather than merely locking up). The actual fix wasn't
    ///   "which decode backend runs per tick" at all — it was removing the
    ///   per-tick decode requirement entirely, which is this rewrite.
    ///
    /// SCRUB COALESCING ("LATEST REQUEST WINS") — STILL IN EFFECT, UNCHANGED
    /// BY THE PROXY REWRITE: a fast scrub drag firing many ScrubToAsync
    /// calls still coalesces to only the latest one actually completing —
    /// even a proxy read is not literally free (a file read plus a full
    /// frame composite), and there is no reason to do that work for a
    /// position that's already stale by the time it would finish.
    ///   1. ScrubToAsync cancels any still-in-flight/queued PREVIOUS scrub
    ///      request the moment a new one arrives (_scrubSupersedeCts),
    ///      linked with the caller's own `ct` — a superseded request's
    ///      OperationCanceledException is swallowed (not the caller's own
    ///      cancellation, so nothing to propagate).
    ///   2. The one-time scrub-session setup — now a real PROXY BUILD for
    ///      any source that doesn't have one cached yet, via
    ///      PrepareScrubProxiesAsync/ScrubProxyCache.GetOrBuildAsync, a
    ///      genuinely slower one-time cost than the old native-size-only
    ///      probe it replaces — is MEMOIZED (_scrubSetupTask, mirroring
    ///      ScrubProxyCache's own cached-Task build coalescing) rather than
    ///      restarted inside every call. Once started it always runs to
    ///      completion regardless of which caller kicked it off or whether
    ///      that caller is later superseded; every call just awaits
    ///      (cancellably, via WaitAsync) whatever the current attempt is —
    ///      the same "cancel the wait, not the shared work" split
    ///      ScrubProxyCache.GetOrBuildAsync itself already uses.
    ///
    /// PrewarmScrubProxiesAsync — the opt-in "generate proxies beforehand"
    /// entry point: builds every video source's scrub proxy ahead of need
    /// (e.g. right after a project loads), so the FIRST scrub/reverse
    /// session doesn't pay any build cost at all. Entirely optional — the
    /// first scrub/reverse session builds whatever's still missing on
    /// demand either way (see EnsureScrubSessionBaseAsync).
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
    /// reverse playback consult a COMPLETELY SEPARATE cache
    /// (ScrubProxyCache) instead — see the section above.
    ///
    /// SWITCHING BETWEEN SCRUB AND PLAYBACK (fixed, KEPT): Play() calls
    /// EndScrubbing() as its very first action, unconditionally, on BOTH
    /// the "resume an existing paused session" path and the "start a
    /// brand-new session" path, BEFORE taking _stateLock. Found in the
    /// field: scrubbing is explicitly allowed while playback is merely
    /// paused (ScrubToAsync's own guard is `_isPlaying &amp;&amp; !IsPaused`, not
    /// `!_isPlaying`) — Play() itself used to never tear an active scrub
    /// session down before (re)starting its own forward-playback session.
    /// Runs OUTSIDE `_stateLock` deliberately: EndScrubbing() blocks
    /// synchronously on `_scrubGate`, and ScrubToAsync can be holding
    /// `_scrubGate` while briefly needing `_stateLock` itself — calling
    /// EndScrubbing() while already holding `_stateLock` would risk a
    /// lock-order inversion deadlock. NOTE this fix now ONLY tears down
    /// `_scrubContentSource` (see SCRUB GPU CONTEXT IS NOW PERSISTENT
    /// below) — it no longer touches the scrub GpuContext at all, since
    /// that is now a persistent, once-created resource for this Playback
    /// instance's whole life.
    ///
    /// EAGER RELEASE ON PAUSE, REVERTED — TRIED, THEN EXPLICITLY UNDONE PER
    /// USER DIRECTION: a real GPU-contention concern was identified where
    /// scrubbing while paused stood up a scrub session's own GpuContext
    /// alongside a paused forward-playback session's still-alive one (two
    /// live GPU contexts at once). A first fix had each loop eagerly
    /// dispose its own GpuContext/SkSurfacePool/decoders the moment it
    /// noticed `pauseGate.IsPaused`, reacquiring only once actually
    /// resumed. That did NOT resolve the freezes still being reported, and
    /// it cost real playback smoothness — resuming from a pause stopped
    /// being free (it paid roughly a fresh Play()-setup cost every time),
    /// which the user explicitly called out as not worth it. REVERTED per
    /// direct instruction: VideoLoopAsync/ReverseVideoLoopAsync are back to
    /// a single GpuContext/SkSurfacePool/decoder set for the WHOLE forward-
    /// playback or reverse session, created once and torn down only at
    /// Stop()/natural end — pausing is, again, a cheap, instantly-resumable
    /// no-op for these resources UNLESS a scrub actually moved the position
    /// during the pause (see SCRUB DURING PAUSE FORCES A REAL SEEK ON
    /// RESUME below — that is a NEW, separate, narrowly-scoped mechanism,
    /// not a reintroduction of this reverted one: it only pays a restart
    /// cost when the position genuinely changed, never on every pause).
    /// SkSourceDecoder.Dispose()'s own WaitForExit-after-Kill fix (see that
    /// class's own remarks) is UNRELATED and STAYS — it's a real
    /// correctness fix regardless of when/whether a decoder gets disposed.
    ///
    /// TWO DEAD-END HYPOTHESES, TRIED AND EITHER REVERTED OR KEPT ON THEIR
    /// OWN MERITS ONLY — NEITHER WAS THE ACTUAL SCRUB LOCKUP: chasing the
    /// same real, reproducible scrub freeze reported repeatedly on real
    /// hardware, two further fixes were tried and BOTH were confirmed, on
    /// real hardware, to change nothing about the freeze:
    ///   (a) Every await in the scrub-reachable async chain (this file,
    ///       ScrubProxyCache, MediaHasher, MediaProbe, FfmpegRunner) was
    ///       given ConfigureAwait(false), on the theory that a caller
    ///       blocking synchronously on ScrubToAsync's Task (e.g.
    ///       `.Wait()`/`.GetAwaiter().GetResult()` from a UI thread) could
    ///       deadlock against a captured SynchronizationContext. REVERTED —
    ///       the real consumer app (a Godot C# game) calls ScrubToAsync
    ///       fire-and-forget from a slider's ValueChanged signal, never
    ///       blocking on it, and Godot's C# integration installs NO
    ///       SynchronizationContext at all (confirmed by the app's own use
    ///       of CallDeferred/SetDeferred everywhere a callback needs to
    ///       reach the main thread) — so this theory never actually applied
    ///       here, and the fix was a no-op for this app's real behavior.
    ///       Removed for cleanliness rather than left in as dead weight.
    ///   (b) GpuContext.Dispose() was changed to wait for the GPU to idle
    ///       (GRContext.Flush() + Submit(syncCpu: true)) before releasing
    ///       the D3D12 device/queue/adapter/factory, on the theory that
    ///       recreating a device shortly after disposing the previous one
    ///       could race that previous device's own driver-side teardown.
    ///       KEPT — it is a real, independent, low-risk correctness
    ///       improvement (see GpuContext's own remarks) — but real-hardware
    ///       testing showed the reported freeze was completely UNCHANGED by
    ///       it, so it is not credited as fixing this bug.
    /// The real cause turned out to be a third, different thing — see GPU
    /// WORK MUST STAY ON ONE THREAD below.
    ///
    /// GPU WORK MUST STAY ON ONE THREAD — THE ACTUAL ROOT CAUSE: since
    /// Godot's C# integration installs no SynchronizationContext (see (a)
    /// above), EVERY `await` anywhere in this file — with or without
    /// ConfigureAwait(false), before or after any fix in this
    /// investigation — has ALWAYS resumed on an arbitrary ThreadPool
    /// thread, not necessarily the same thread as before that await. That
    /// means every GPU-touching call this file makes (GpuContext.Create,
    /// SkSurfacePool's Rent/CreateSurface, SkFrameCompositor.RenderFrame,
    /// GpuContext.Dispose) could already land on a DIFFERENT OS thread than
    /// the call immediately before or after it, purely as an artifact of
    /// how the .NET ThreadPool happens to schedule continuations — this was
    /// true from the very first version of the scrub rewrite, independent
    /// of every fix tried above. Skia's GrDirectContext (GRContext in
    /// SkiaSharp) is not documented as safe for that usage pattern:
    /// sequential-but-cross-thread access to one GRContext, its SKSurfaces,
    /// and the D3D12 command queue backing it, with nothing pinning it to
    /// one thread, is a real, plausible source of a driver-level hang —
    /// more likely to actually manifest the more real GPU work a given call
    /// submits, which lines up with the reported pattern (a cheap/short
    /// first scrub tends to survive; a slower/longer one is more likely to
    /// get unlucky; once ANY frame has succeeded, later calls in the same
    /// process keep landing on threads the pool already has warmed up for
    /// this workload, which is why it then "stays fixed"). FIX:
    /// GpuThreadDispatcher (Composite/GpuThreadDispatcher.cs) confines every
    /// GPU-touching call for a given GpuContext/SkSurfacePool pair to ONE
    /// dedicated background thread, for as long as that pair is alive —
    /// used for the scrub GPU context/pool (`_scrubGpuThread`, persistent —
    /// see SCRUB GPU CONTEXT IS NOW PERSISTENT below) and for a
    /// session-scoped dispatcher inside both VideoLoopAsync and
    /// ReverseVideoLoopAsync (created and disposed alongside that session's
    /// own GpuContext/SkSurfacePool). Every PrefetchAsync/RenderFrame/
    /// Create/Dispose call for a given context now happens on that context's
    /// own single thread, every time, structurally — not by hoping the
    /// ThreadPool schedules favorably. CONFIRMED ON REAL HARDWARE — the
    /// user reported the reported freeze/lockup is completely gone after
    /// this fix.
    ///
    /// SCRUB GPU CONTEXT IS NOW PERSISTENT, PER EXPLICIT USER REQUEST:
    /// `_scrubGpuContext`/`_scrubSurfacePool`/`_scrubGpuThread` are created
    /// AT MOST ONCE per Playback instance (lazily, on first scrub/reverse
    /// session) and reused by every scrub session after that — EndScrubbing
    /// no longer disposes them at all; only Playback.Dispose() does, once,
    /// for real. This has two independent benefits: it directly avoids ever
    /// repeatedly creating and destroying a D3D12 device for scrubbing at
    /// all (so the earlier dispose-then-recreate race window this
    /// investigation chased in (b) above simply cannot occur for scrubbing
    /// any more, regardless of whether it was ever real), and it means a
    /// scrub session's own setup (EnsureScrubSessionBaseAsync/
    /// BuildScrubSessionAsync) only needs to rebuild `_scrubContentSource`
    /// (proxy readers/static images/nested renderers — plain file handles,
    /// no GPU) on each EndScrubbing()/rebuild cycle, which is both cheaper
    /// and has no GPU-lifetime implications at all. ASSUMES RenderSettings.
    /// Resolution/Framerate stay constant for this Playback instance's
    /// whole life — true for how this class is actually constructed today
    /// (see EditSharp's own usage), but flagged here since a persisted
    /// SkSurfacePool sized for the FIRST scrub session's width/height would
    /// silently be wrong for a later session at a different resolution;
    /// revisit if RenderSettings ever needs to change on a live instance.
    ///
    /// SCRUBBING WHILE PAUSED UPDATES Position: ScrubToAsync reports the
    /// scrubbed-to position into the SAME PlaybackReferenceClock a
    /// live/paused session uses (`_referenceClock?.Report(position)`, in
    /// addition to `_lastKnownPosition`) — previously it only wrote
    /// `_lastKnownPosition`, which the public `Position` getter never
    /// actually reads while `_referenceClock` is non-null (i.e. for the
    /// entire lifetime of a session, paused included). ALSO bumps
    /// `_scrubGeneration` under the same lock — see SCRUB DURING PAUSE
    /// FORCES A REAL SEEK ON RESUME immediately below, which is what
    /// actually makes a paused scrub "count" the next time Play() is
    /// called.
    ///
    /// SCRUB DURING PAUSE FORCES A REAL SEEK ON RESUME (NEW FIX — root
    /// cause of two reported bugs, both now fixed): previously, a scrub
    /// taken while paused updated `_referenceClock`/Position (see directly
    /// above) but NOTHING ELSE — the video loop's persistent decode pipe
    /// (SkClipContentSource/SkSourceDecoder, which can only move FORWARD)
    /// and the audio engine's own byte-offset pump both kept whatever
    /// position they were at before the pause, completely unaware a scrub
    /// had ever happened. A plain Play() resume just released the pause
    /// gate and resumed the wall clock in place, so BOTH streams simply
    /// continued from the PRE-scrub position — the scrub was a preview
    /// only, never an actual seek (bug 1: "scrubbing should change
    /// playback position, but it doesn't"). Worse, the stale
    /// `_referenceClock` value left behind by the scrub caused a SEPARATE,
    /// visible symptom for a follower stream: PlaybackReferenceClock.
    /// Position resumes extrapolating forward, in real time, from wherever
    /// it was last Report()'d — if a scrub landed EARLIER than the actual
    /// pre-pause stopped position, that value starts BELOW the follower's
    /// own fixed target position, so the follower's `gap = target -
    /// referenceClock.Position` computes a large POSITIVE gap and sleeps
    /// (in one big Task.Delay, not a poll loop — see VideoLoopAsync's/
    /// PlaybackAudioEngine.PumpAsync's own wait logic) for approximately
    /// the real-time distance between the scrub position and the actual
    /// resume position, before the leader's own next report ever gets a
    /// chance to correct it — a real, reproducible multi-second stall
    /// between the leader (audio, in the default SyncToAudio mode)
    /// resuming essentially immediately and the follower (video) resuming
    /// only after that stale gap has fully elapsed (bug 2: "significant
    /// delay between audio starting back up and video starting back up ...
    /// ending a scrub at an earlier position [than where playback
    /// stopped]"). A scrub to a LATER position than the stopped position
    /// produces a negative/zero gap instead, so the follower never waits —
    /// which is exactly why the user observed the delay ONLY for the
    /// "earlier" case and an instant (but still wrong-position, per bug 1)
    /// resume for the "later" case: both symptoms trace to this one gap.
    ///
    /// FIX: `_scrubGeneration` (bumped by ScrubToAsync under `_stateLock`,
    /// alongside its Report() call) and `_scrubGenerationAtPause` (a
    /// snapshot of `_scrubGeneration` taken by Pause()) together detect
    /// "did an actual scrub happen since this pause started" — a plain
    /// int-equality check, immune to any timing race with the leader's own
    /// periodic Report() calls (which never touch `_scrubGeneration`).
    /// Play()'s resume branch (`_isPlaying &amp;&amp; startPosition == null`)
    /// now checks this first:
    ///   - NO scrub happened (generations match): unchanged, cheap,
    ///     instant in-place resume — `_pauseGate.Resume()` +
    ///     `_referenceClock.ResumeWallClock()`, exactly as before this fix.
    ///   - A scrub DID happen (generations differ): the persistent decode
    ///     pipe genuinely cannot jump to the new position on its own, so
    ///     this falls through to the SAME stop+restart path a fresh
    ///     `Play(startPosition)` call already uses — `startPosition` is set
    ///     to `_referenceClock.Position` (the scrubbed-to position, frozen
    ///     since the wall clock never resumed) and the existing Stop() +
    ///     rebuild-from-scratch machinery below takes it from there: a
    ///     brand-new PlaybackReferenceClock initialized directly to that
    ///     position, a brand-new audio pump starting its byte offset from
    ///     it, and a brand-new video/reverse loop with decoders seeked via
    ///     ComputeSeekOffsets(position) — the exact same well-tested
    ///     startup path the very first Play() call already goes through,
    ///     so there is no stale-clock/gap artifact left to produce bug 2
    ///     either. Named trade-off: resuming after an actual scrub-during-
    ///     pause now costs roughly a fresh Play() setup (new decoders, new
    ///     GpuContext-dispatcher work) — but ONLY when the position
    ///     genuinely changed. A plain pause/resume with no scrub in between
    ///     is completely unaffected and stays exactly as cheap as it always
    ///     was; this is a narrowly-scoped fix, not a reintroduction of
    ///     EAGER RELEASE ON PAUSE, REVERTED above.
    ///
    /// KNOWN GAPS — tracked, not hidden:
    ///   1. REVERSE AUDIO is not implemented — reverse is video-only, see
    ///      REVERSE PLAYBACK above. Video itself IS supported.
    ///   2. ARBITRARY SPEED (audio tracking Speed != 1, Speed &gt; 0) remains
    ///      a MUST-HAVE, not deferred-maybe.
    ///   3. Seeking to a nonzero start position may need the audio
    ///      composition itself to carry a seek offset.
    ///   4. TRUE FRAME-SKIPPING remains open.
    ///   5. PlaybackMode WIRING — CLOSED (SyncToAudio/EveryFrame both real).
    ///   6. SCRUBBING — mechanism CLOSED (see SCRUB/REVERSE VIA RAW SCRUB
    ///      PROXIES above; SupportsScrubbing is always true now). The real,
    ///      repeatedly-reproduced lockup is CONFIRMED FIXED (by the user, on
    ///      real hardware) via GPU WORK MUST STAY ON ONE THREAD above
    ///      (GpuThreadDispatcher + a persistent scrub GpuContext). Two
    ///      earlier fixes in this same investigation (ConfigureAwait(false)
    ///      throughout the scrub async chain; a GPU-idle wait in
    ///      GpuContext.Dispose()) were both confirmed on real hardware NOT
    ///      to change this freeze — the first was reverted as unnecessary,
    ///      the second was kept on its own independent merits only (see TWO
    ///      DEAD-END HYPOTHESES above).
    ///   7. CLOSED — see SCRUB DURING PAUSE FORCES A REAL SEEK ON RESUME
    ///      above. Resuming playback after scrubbing while paused now
    ///      actually resumes from the scrubbed position, and the A/V
    ///      startup-delay artifact that came along with the old bug is
    ///      gone too. NOT YET CONFIRMED ON REAL HARDWARE — reported as
    ///      believed-fixed pending the user's own testing, same as every
    ///      other fix in this file.
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

        // Always true now — see class remarks, SCRUB/REVERSE VIA RAW SCRUB
        // PROXIES. Kept (rather than removed) purely for API compatibility
        // with existing callers that gate ScrubToAsync on this; safe to
        // stop checking it.
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

        // See class remarks, SCRUB DURING PAUSE FORCES A REAL SEEK ON
        // RESUME. `_scrubGeneration` is bumped once per successful
        // ScrubToAsync delivery (under `_stateLock`, alongside its
        // `_referenceClock.Report(position)` call); `_scrubGenerationAtPause`
        // is a snapshot of it taken by Pause(). Play()'s resume branch
        // compares the two to decide whether an actual scrub — not just the
        // leader's own periodic position reports — happened since the
        // session was paused.
        private int _scrubGeneration;
        private int _scrubGenerationAtPause;

        private readonly SemaphoreSlim _scrubGate = new(1, 1);

        // PERSISTENT for this Playback instance's whole life — see class
        // remarks, SCRUB GPU CONTEXT IS NOW PERSISTENT. Created at most
        // once (lazily, in BuildScrubSessionAsync), reused by every scrub/
        // reverse session after that, and disposed only in Dispose(). All
        // three GPU-touching calls that use `_scrubGpuContext`/
        // `_scrubSurfacePool` MUST go through `_scrubGpuThread` — see class
        // remarks, GPU WORK MUST STAY ON ONE THREAD.
        private GpuThreadDispatcher? _scrubGpuThread;
        private GpuContext? _scrubGpuContext;
        private SkSurfacePool? _scrubSurfacePool;

        // Rebuilt every scrub session (EndScrubbing -> next
        // BuildScrubSessionAsync) — plain file handles/dictionaries, no GPU
        // resources, so there's no reason to make these persistent too.
        private ConcurrentDictionary<Guid, ScrubProxyEntry>? _scrubProxies;
        private ScrubFrameSource? _scrubContentSource;

        // Memoized one-time scrub-session setup — see class remarks, SCRUB
        // COALESCING. Started at most once per session; every ScrubToAsync
        // call awaits whichever attempt is current rather than starting its
        // own. Reset to null by EndScrubbing() so the NEXT session rebuilds
        // `_scrubContentSource`/`_scrubProxies` (the GPU context/pool/thread
        // above are untouched by that reset — see SCRUB GPU CONTEXT IS NOW
        // PERSISTENT).
        private Task? _scrubSetupTask;

        // The most recent ScrubToAsync call's own supersession token — see
        // class remarks, SCRUB COALESCING. Cancelled (and replaced) every
        // time a new ScrubToAsync call arrives, so an older, now-stale
        // request stops waiting/decoding promptly instead of queueing
        // behind _scrubGate.
        private CancellationTokenSource? _scrubSupersedeCts;

        public void Play(TimeSpan? startPosition = null)
        {
            // See class remarks, SWITCHING BETWEEN SCRUB AND PLAYBACK —
            // must run before touching _stateLock at all (lock-order
            // reasons, see remarks), and unconditionally, on both the
            // resume-existing-session path and the fresh-start path below:
            // scrubbing is allowed while merely paused, so either path can
            // otherwise race an active scrub session's own content source.
            EndScrubbing();

            lock (_stateLock)
            {
                if (_isPlaying && startPosition == null)
                {
                    if (_scrubGeneration == _scrubGenerationAtPause)
                    {
                        // No scrub happened since this pause started —
                        // same cheap, instant, in-place resume as always.
                        _pauseGate?.Resume();
                        _referenceClock?.ResumeWallClock();
                        return;
                    }

                    // See class remarks, SCRUB DURING PAUSE FORCES A REAL
                    // SEEK ON RESUME: a scrub landed while paused, and the
                    // persistent decode pipe this session already holds
                    // has no way to actually jump to that position — fall
                    // through to the stop+restart path below with
                    // `startPosition` set to the scrubbed-to position,
                    // exactly as if the caller had called
                    // Play(scrubbedPosition) directly.
                    startPosition = _referenceClock?.Position;
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
                // Snapshot BEFORE stopping the wall clock — see class
                // remarks, SCRUB DURING PAUSE FORCES A REAL SEEK ON RESUME.
                _scrubGenerationAtPause = _scrubGeneration;
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
        /// Always succeeds now — see class remarks, SCRUB/REVERSE VIA RAW
        /// SCRUB PROXIES. Kept for API compatibility with existing callers
        /// that gate ScrubToAsync on this; safe to stop calling.
        /// </summary>
        public Task<bool> RefreshScrubbingSupportAsync(CancellationToken ct = default)
        {
            SupportsScrubbing = true;
            return Task.FromResult(true);
        }

        /// <summary>
        /// Builds every video source referenced by Timeline's own scrub
        /// proxy ahead of need, so a later scrub/reverse session's own
        /// setup (EnsureScrubSessionBaseAsync) finds everything already
        /// cached and pays no build cost at all — the opt-in "generate
        /// proxies beforehand" entry point (see class remarks). Entirely
        /// optional: a scrub/reverse session builds whatever's still
        /// missing on demand either way. Safe to call at any time,
        /// including while a scrub session is already active or playback
        /// is running — it only ever reads/builds via ScrubProxyCache, it
        /// never touches this instance's own scrub-session state.
        /// </summary>
        public Task PrewarmScrubProxiesAsync(CancellationToken ct = default) =>
            Task.WhenAll(EnumerateVideoSourcePaths(Timeline)
                .Select(path => ScrubProxyCache.PrewarmAsync(path, RenderSettings.HardwareAccelerator, ct)));

        /// <summary>
        /// Renders and delivers one frame at `position`, via VideoFrame.
        /// SAFE TO CALL RAPIDLY — e.g. once per pointer-move during a
        /// scrubber drag: each call supersedes (cancels) whatever previous
        /// call hasn't finished yet, so only the LATEST requested position
        /// ever actually completes and gets delivered — see class remarks,
        /// SCRUB COALESCING. A superseded call's Task completes normally
        /// (no exception) rather than throwing — only `ct` (if the CALLER
        /// explicitly cancels it) propagates as a real cancellation.
        ///
        /// Also updates Position (both `_lastKnownPosition` and, when a
        /// session is active/paused, the shared PlaybackReferenceClock),
        /// and bumps `_scrubGeneration` — see class remarks, SCRUBBING
        /// WHILE PAUSED UPDATES Position and SCRUB DURING PAUSE FORCES A
        /// REAL SEEK ON RESUME.
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

                    // `_scrubGpuThread` is guaranteed non-null here —
                    // EnsureScrubSessionBaseAsync always creates it (once)
                    // before returning. See class remarks, GPU WORK MUST
                    // STAY ON ONE THREAD.
                    (byte[] buffer, int length) = await ComposeInstantFrameAsync(
                        _scrubContentSource!, _scrubSurfacePool!, _scrubGpuThread!,
                        position, width, height, fps, linked);

                    try
                    {
                        OnVideoFrame(new VideoFrameEventArgs(buffer, length, width, height, position));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    lock (_stateLock)
                    {
                        _lastKnownPosition = position;

                        // See class remarks, SCRUBBING WHILE PAUSED UPDATES
                        // Position: _referenceClock stays non-null (and is
                        // what the public Position getter actually reads)
                        // for the whole lifetime of a session, paused
                        // included — without this, scrubbing while paused
                        // silently updated a value nothing ever read.
                        _referenceClock?.Report(position);

                        // See class remarks, SCRUB DURING PAUSE FORCES A
                        // REAL SEEK ON RESUME: this is the ONLY place
                        // `_scrubGeneration` is bumped, and it happens
                        // under the same lock/same moment as the Report()
                        // call above so Play()'s resume-branch comparison
                        // can never observe one without the other.
                        _scrubGeneration++;
                    }
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
        /// Resolves the FrameState at `position` and composites exactly one
        /// frame at it. Shared by ScrubToAsync (on demand) and
        /// ReverseVideoLoopAsync (on its own pacing timer) — both are,
        /// mechanically, "compose one frame at an arbitrary position,
        /// instantly."
        ///
        /// EVERY GPU-TOUCHING STEP — including PrefetchAsync, which for
        /// generator/noise/nested-timeline nodes rents/creates surfaces
        /// through `pool` and therefore through its backing GRContext, not
        /// just SkFrameCompositor.RenderFrame itself — runs as ONE unit of
        /// work on `gpuThread`, the dedicated thread that owns `pool`'s
        /// GpuContext. See class remarks, GPU WORK MUST STAY ON ONE THREAD.
        /// PrefetchAsync is blocked on synchronously (`.GetAwaiter().
        /// GetResult()`) INSIDE that unit of work rather than awaited —
        /// safe only because ScrubFrameSource's own remarks document it as
        /// fully synchronous under the hood (always returns
        /// Task.CompletedTask), so this never actually blocks a thread on
        /// real I/O.
        /// </summary>
        private Task<(byte[] Buffer, int Length)> ComposeInstantFrameAsync(
            ScrubFrameSource contentSource, SkSurfacePool pool, GpuThreadDispatcher gpuThread,
            TimeSpan position, int width, int height, int fps, CancellationToken ct = default)
        {
            int frameIndex = (int)(position.TotalSeconds * fps);
            FrameState state = FrameStateResolver.Resolve(Timeline, frameIndex, fps);

            return gpuThread.RunAsync(() =>
            {
                foreach (FrameChannel channel in state.Channels)
                {
                    foreach (FrameClip frameClip in channel.Clips)
                    {
                        if (frameClip.Clip is VideoClip videoClip)
                        {
                            contentSource.PrefetchAsync(videoClip, frameClip.ClipSeconds, width, height, pool, ct)
                                .GetAwaiter().GetResult();
                        }
                    }
                }

                (byte[] buffer, int length) = SkFrameCompositor.RenderFrame(
                    state, contentSource, width, height, fps, pool);

                contentSource.ClearPrefetch();

                return (buffer, length);
            });
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

        /// <summary>
        /// Rebuilds `_scrubContentSource`/`_scrubProxies` every session, but
        /// creates `_scrubGpuThread`/`_scrubGpuContext`/`_scrubSurfacePool`
        /// AT MOST ONCE for this Playback instance's whole life — see class
        /// remarks, SCRUB GPU CONTEXT IS NOW PERSISTENT. GpuContext.Create
        /// and the SkSurfacePool constructor both run on `_scrubGpuThread`
        /// itself (see class remarks, GPU WORK MUST STAY ON ONE THREAD), so
        /// this method's own await here is what actually makes that
        /// thread-confinement possible.
        /// </summary>
        private async Task BuildScrubSessionAsync(int width, int height)
        {
            var proxies = new ConcurrentDictionary<Guid, ScrubProxyEntry>();

            await PrepareScrubProxiesAsync(Timeline, RenderSettings.HardwareAccelerator, proxies);

            _scrubProxies = proxies;
            _scrubContentSource = new ScrubFrameSource(
                RenderSettings.Framerate, RenderSettings.HardwareAccelerator, proxies);

            if (_scrubGpuContext == null)
            {
                _scrubGpuThread ??= new GpuThreadDispatcher("EditSharp-ScrubGPU");

                (GpuContext context, SkSurfacePool pool) = await _scrubGpuThread.RunAsync(() =>
                {
                    GpuContext ctx = GpuContext.Create(
                        RenderSettings.HardwareAccelerator, RenderSettings.GpuAdapterIndex);
                    var surfacePool = new SkSurfacePool(ctx.GRContext, width, height, Timeline.VideoChannels.Count);
                    return (ctx, surfacePool);
                });

                _scrubGpuContext = context;
                _scrubSurfacePool = pool;
            }
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

                _scrubProxies = null;

                // `_scrubGpuThread`/`_scrubGpuContext`/`_scrubSurfacePool`
                // are DELIBERATELY NOT touched here — see class remarks,
                // SCRUB GPU CONTEXT IS NOW PERSISTENT. They live for this
                // Playback instance's whole life; only Dispose() tears them
                // down, once, for real.
            }
            finally
            {
                _scrubGate.Release();
            }
        }

        /// <summary>
        /// Resolves (building on a cache miss — BLOCKING; see class remarks,
        /// SCRUB/REVERSE VIA RAW SCRUB PROXIES) every video source
        /// referenced by `timeline`'s own scrub proxy via ScrubProxyCache.
        /// Deliberately NOT RenderContentPreparation.PrepareContentAsync,
        /// which probes native size/decode plan and consults
        /// OptimizedMediaCache — none of that applies here at all any more;
        /// this only needs each source's already-resolved-or-built
        /// ScrubProxyEntry.
        /// </summary>
        private static async Task PrepareScrubProxiesAsync(
            Timeline timeline, HardwareAccelerator hwAccel,
            ConcurrentDictionary<Guid, ScrubProxyEntry> proxies)
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
                        tasks.Add(ResolveOneAsync(media));
                    }
                }
            }

            await Task.WhenAll(tasks);

            async Task ResolveOneAsync(VideoSourceNode media)
            {
                proxies[media.Id] = await ScrubProxyCache.GetOrBuildAsync(media.Source.Path, hwAccel);
            }
        }

        /// <summary>Every distinct Video-type source path referenced by `timeline` — used by PrewarmScrubProxiesAsync.</summary>
        private static IEnumerable<string> EnumerateVideoSourcePaths(Timeline timeline) =>
            timeline.VideoChannels
                .SelectMany(channel => channel.Clips)
                .OfType<VideoClip>()
                .SelectMany(video => video.Graph.Nodes.OfType<VideoSourceNode>())
                .Where(media => media.Source.Type == SourceType.Video)
                .Select(media => media.Source.Path)
                .Distinct();

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
        ///
        /// GpuContext/SkSurfacePool/decoders (contentSource) are `using`-
        /// scoped for the WHOLE session here, created once and torn down
        /// only when this method returns (Stop()/natural end/fault) — a
        /// pause is a cheap, instantly-resumable no-op for these resources.
        /// This was briefly changed to an eager-release-on-pause design and
        /// then explicitly REVERTED per direct user instruction — see class
        /// remarks, EAGER RELEASE ON PAUSE, REVERTED, for the full history
        /// and why.
        ///
        /// GPU CREATE/RENDER/DISPOSE ALL RUN ON ONE DEDICATED THREAD
        /// (`videoGpuThread`, session-scoped — created and disposed
        /// alongside `gpuContext`/`surfacePool` themselves) — see class
        /// remarks, GPU WORK MUST STAY ON ONE THREAD. This is the same fix
        /// applied to scrubbing, extended here for consistency: this loop's
        /// own awaits (Task.Delay, PlaybackPauseGate.WaitIfPausedAsync) can
        /// resume on a different ThreadPool thread each time just like
        /// ScrubToAsync's could, since nothing in this process installs a
        /// SynchronizationContext — so without this, every per-frame
        /// SkFrameCompositor.RenderFrame call here carried the same
        /// cross-thread GRContext hazard scrubbing did.
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

                    using var videoGpuThread = new GpuThreadDispatcher("EditSharp-VideoGPU");

                    GpuContext gpuContext = await videoGpuThread.RunAsync(() =>
                        GpuContext.Create(RenderSettings.HardwareAccelerator, RenderSettings.GpuAdapterIndex));
                    SkSurfacePool surfacePool = await videoGpuThread.RunAsync(() =>
                        new SkSurfacePool(gpuContext.GRContext, width, height, Timeline.VideoChannels.Count));

                    try
                    {
                        int startFrame = (int)(startPosition.TotalSeconds * fps);
                        int totalFrames = Math.Max(1, (int)Math.Ceiling(Timeline.Duration.TotalSeconds * fps));

                        FrameState warmupState = FrameStateResolver.Resolve(Timeline, startFrame, fps);
                        (byte[] warmupBuffer, int warmupLength) = await videoGpuThread.RunAsync(() =>
                            SkFrameCompositor.RenderFrame(warmupState, contentSource, width, height, fps, surfacePool));
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

                            (byte[] buffer, int length) = await videoGpuThread.RunAsync(() =>
                                SkFrameCompositor.RenderFrame(state, contentSource, width, height, fps, surfacePool));

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
                    finally
                    {
                        await videoGpuThread.RunAsync(() =>
                        {
                            surfacePool.Dispose();
                            gpuContext.Dispose();
                        });
                    }
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
        /// timeline, reusing the exact same instant, decoder-less raw scrub
        /// proxy read ScrubToAsync uses (ScrubFrameSource / ScrubProxyCache
        /// / ScrubProxyReader), NOT the persistent forward-only
        /// SkSourceDecoder pipe VideoLoopAsync uses, which structurally
        /// cannot move backward at all. See Playback's class remarks,
        /// SCRUB/REVERSE VIA RAW SCRUB PROXIES.
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
        /// work promptly (same cancellation plumbing ScrubToAsync uses —
        /// see class remarks, SCRUB COALESCING).
        ///
        /// VISUAL CHARACTER: every step reads its OWN scrub-proxy frame at
        /// its own exact position — see class remarks for why this now
        /// looks like a smooth, evenly-paced backward step at the proxy's
        /// own fixed sample rate, not the earlier keyframe-snapped "fast
        /// rewind" jumpiness.
        ///
        /// GpuContext/SkSurfacePool/contentSource are session-scoped here
        /// too — same revert as VideoLoopAsync, see class remarks, EAGER
        /// RELEASE ON PAUSE, REVERTED. Uses its OWN session-scoped
        /// `reverseGpuThread` (NOT the persistent `_scrubGpuThread` scrub
        /// sessions use) — a reverse session already creates and tears down
        /// its own GpuContext/SkSurfacePool every time it starts/stops
        /// (unlike scrubbing, this wasn't changed to be persistent, since
        /// the user's request to preserve the GPU context was specifically
        /// about scrubbing), so a matching session-scoped dispatcher is the
        /// right shape here — see class remarks, GPU WORK MUST STAY ON ONE
        /// THREAD.
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
                    var proxies = new ConcurrentDictionary<Guid, ScrubProxyEntry>();

                    await PrepareScrubProxiesAsync(Timeline, RenderSettings.HardwareAccelerator, proxies);

                    using var contentSource = new ScrubFrameSource(
                        fps, RenderSettings.HardwareAccelerator, proxies);

                    using var reverseGpuThread = new GpuThreadDispatcher("EditSharp-ReverseGPU");

                    GpuContext gpuContext = await reverseGpuThread.RunAsync(() =>
                        GpuContext.Create(RenderSettings.HardwareAccelerator, RenderSettings.GpuAdapterIndex));
                    SkSurfacePool surfacePool = await reverseGpuThread.RunAsync(() =>
                        new SkSurfacePool(gpuContext.GRContext, width, height, Timeline.VideoChannels.Count));

                    try
                    {
                        int startFrame = (int)(startPosition.TotalSeconds * fps);

                        (byte[] warmupBuffer, int warmupLength) = await ComposeInstantFrameAsync(
                            contentSource, surfacePool, reverseGpuThread, startPosition, width, height, fps, token);
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
                                contentSource, surfacePool, reverseGpuThread, framePosition, width, height, fps, token);

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
                    finally
                    {
                        await reverseGpuThread.RunAsync(() =>
                        {
                            surfacePool.Dispose();
                            gpuContext.Dispose();
                        });
                    }
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

        /// <summary>
        /// Tears down EVERYTHING, including the persistent scrub GPU
        /// resources EndScrubbing() deliberately leaves alone — see class
        /// remarks, SCRUB GPU CONTEXT IS NOW PERSISTENT. The final
        /// GpuContext/SkSurfacePool disposal still runs on
        /// `_scrubGpuThread` itself (consistent with every other GPU call
        /// in this file — see GPU WORK MUST STAY ON ONE THREAD), blocked on
        /// synchronously since Dispose() is conventionally synchronous;
        /// this is the one place in this file where blocking on a
        /// dispatcher Task is appropriate, since the dispatcher's thread
        /// isn't waiting on the calling thread for anything.
        /// </summary>
        public void Dispose()
        {
            Stop();
            EndScrubbing();

            if (_scrubGpuThread != null)
            {
                try
                {
                    _scrubGpuThread.RunAsync(() =>
                    {
                        _scrubSurfacePool?.Dispose();
                        _scrubGpuContext?.Dispose();
                    }).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    EditSharpConfig.Logger.LogVerbose(
                        $"Playback.Dispose: scrub GPU teardown threw: {ex.Message}");
                }
                finally
                {
                    _scrubSurfacePool = null;
                    _scrubGpuContext = null;
                    _scrubGpuThread.Dispose();
                    _scrubGpuThread = null;
                }
            }
        }
    }
}