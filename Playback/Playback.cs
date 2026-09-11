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
using EditSharp.Components.Channels;
using EditSharp.Components.Clips;
using EditSharp.Components.Nodes.Sources;
using EditSharp.Caching.ScrubProxy;
using EditSharp.Compositing;
using EditSharp.Compositing.Gpu;
using EditSharp.Compositing.Sources;
using EditSharp.Rendering;
using EditSharp.Video;

namespace EditSharp.Playback
{
    /// <summary>
    /// Audio-based playback for timelines.
    ///
    /// Built directly on top of Renderer's Skia compositor primitives
    /// (ContentPreparation, ClipContentSource, FrameCompositor,
    /// GpuContext, SurfacePool) rather than re-deriving them.
    ///
    /// "CLIPS ARE GRAPHS" REWRITE: ContentPreparation's
    /// nativeSizes/decodePlans/decodeSourcePaths dictionaries are now keyed
    /// by InputNode Id (Guid), not by Clip — a VideoClip's graph can contain
    /// more than one VideoSourceNode; staticImagePaths is gone entirely (text
    /// rasterization moved into ClipContentSource itself). The two
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
    /// own remarks). The two SurfacePool seedCount call sites below
    /// (VideoLoopAsync, ReverseVideoLoopAsync) now seed off
    /// Timeline.VideoChannels.Count specifically rather than the old mixed
    /// Timeline.Channels.Count — an AudioChannel never needs a GPU-backed
    /// canvas surface, so counting it toward the warm-start heuristic never
    /// bought anything (see SurfacePool's own remarks: this is a warm-start
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
    /// below for how a subsequent Play() actually resumes from it now. AND
    /// a scrub call sets Position FIRST, before it does anything else at
    /// all — see SCRUB SETS POSITION FIRST, UNCONDITIONALLY below.
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
    /// paused session's GpuContext/SurfacePool/decoders stay fully alive
    /// (see EAGER RELEASE ON PAUSE, REVERTED below) — pausing is meant to
    /// be, and is, an essentially free, instantly-resumable operation
    /// UNLESS a scrub actually moved the position during that pause — see
    /// SCRUB DURING PAUSE FORCES A REAL SEEK ON RESUME below.
    ///
    /// STATE (PlaybackState) — REPLACED THE EARLIER IsPlaying/IsPaused
    /// BOOLEAN PAIR (decided in conversation): this class used to expose
    /// two independently-readable booleans (`IsPlaying`, and `IsPaused`
    /// derived from `_pauseGate?.IsPaused`), which meant a caller wanting
    /// "is this actually actively playing right now" had to combine them
    /// itself (`IsPlaying &amp;&amp; !IsPaused`) — and internally, this class had
    /// its own `_isPlaying` bool tracking "a session exists" completely
    /// separately from `_pauseGate`'s own paused/not-paused bit, two pieces
    /// of state that could only ever legally combine into three real
    /// situations (no session; session, unpaused; session, paused) despite
    /// the boolean pair's own state space technically allowing four. A
    /// single `PlaybackState State` (see that enum's own remarks) replaces
    /// both: `Inactive`/`Playing`/`Paused` cover exactly those three real
    /// situations with no illegal combination possible, and `Scrubbing` is
    /// now a real, first-class fourth state (see SCRUB STATE IS NOW A REAL
    /// STATE, NOT AN ORTHOGONAL FLAG below) rather than something that used
    /// to happen ambiently underneath whatever `IsPlaying`/`IsPaused` said.
    /// `_pauseGate` itself is UNCHANGED and still exists — it remains the
    /// actual mechanism VideoLoopAsync/ReverseVideoLoopAsync poll to block
    /// producing new frames while paused; `_state` is the coarse, externally-
    /// visible reflection of what's currently true, kept in sync with every
    /// place this class used to flip `_isPlaying` or touch `_pauseGate`.
    ///
    /// SCRUB STATE IS NOW A REAL STATE, NOT AN ORTHOGONAL FLAG (part of the
    /// STATE change above): ScrubToAsync's own guard — previously
    /// `_isPlaying &amp;&amp; !IsPaused` — is now simply `State ==
    /// PlaybackState.Playing` (throws in that one case, exactly the same
    /// set of situations as before: scrubbing is allowed whenever no
    /// session exists at all, OR the existing session is paused). For the
    /// duration of composing and delivering ONE scrubbed-to frame, `_state`
    /// is set to `PlaybackState.Scrubbing` (saving whatever it was
    /// immediately before — `Inactive` or `Paused` — under `_stateLock`)
    /// and restored to that saved value once the render finishes, whether
    /// it succeeds or fails. Because actual scrub RENDERING is already
    /// serialized through `_scrubGate` (a plain SemaphoreSlim — see SCRUB
    /// COALESCING below), only one ScrubToAsync call is ever inside that
    /// bracket at a time, so this save/restore can never race a second
    /// concurrent one.
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
    /// backing REAL forward playback (SourceDecoder.Start/NextFrame, and
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
    ///   2. The one-time scrub-session setup — its own GPU context/pool
    ///      (persistent, see SCRUB GPU CONTEXT IS NOW PERSISTENT below) plus
    ///      a fresh ScrubFrameSource — is MEMOIZED (_scrubSetupTask,
    ///      mirroring ScrubProxyCache's own cached-Task build coalescing)
    ///      rather than restarted inside every call. Once started it always
    ///      runs to completion regardless of which caller kicked it off or
    ///      whether that caller is later superseded; every call just awaits
    ///      (cancellably, via WaitAsync) whatever the current attempt is —
    ///      the same "cancel the wait, not the shared work" split
    ///      ScrubProxyCache.GetOrBuildAsync itself already uses. Individual
    ///      SOURCE proxy builds are no longer part of what this setup
    ///      waits ON at all — see SCRUB PROXY BUILDS RUN ON A REAL
    ///      BACKGROUND THREAD, NEVER BLOCK SESSION STARTUP below.
    ///
    /// PrewarmScrubProxiesAsync — the opt-in "generate proxies beforehand"
    /// entry point: builds every video source's scrub proxy ahead of need
    /// (e.g. right after a project loads), so the FIRST scrub/reverse
    /// session doesn't pay any build cost at all. Entirely optional — the
    /// first scrub/reverse session builds whatever's still missing on
    /// demand either way (see EnsureScrubSessionBaseAsync). UNLIKE the
    /// session-startup path below, THIS entry point is still fully
    /// awaited/blocking for its own caller — that's the point of calling it
    /// explicitly ahead of time.
    ///
    /// SCRUB PROXY BUILDS RUN ON A REAL BACKGROUND THREAD, NEVER BLOCK
    /// SESSION STARTUP (fixed here, real-world regression found in
    /// testing): a scrub/reverse session's own setup used to AWAIT every
    /// referenced source's scrub proxy build INLINE (the old
    /// PrepareScrubProxiesAsync -> Task.WhenAll -> per-source
    /// ScrubProxyCache.GetOrBuildAsync) before EnsureScrubSessionBaseAsync
    /// (and therefore ScrubToAsync/ReverseVideoLoopAsync's own startup)
    /// would ever complete — for a source with no cached proxy yet, that's
    /// a real, potentially multi-second decode+encode pass (see
    /// ScrubProxyCache.BuildAsync), and testing found it visibly HUNG THE
    /// CALLING THREAD for that whole duration, not merely responded slowly.
    /// NEW EVIDENCE surfaced by that same test run, worth recording since it
    /// contradicts an earlier conclusion in this file: TWO DEAD-END
    /// HYPOTHESES below, point (a), concluded Godot's C# integration
    /// installs NO SynchronizationContext at all. A production crash log
    /// captured DURING THIS ROUND showed `Godot.GodotSynchronizationContext`
    /// / `Godot.GodotTaskScheduler` (`ExecutePendingContinuations`/
    /// `Activate`, both driven from the engine's own per-frame
    /// `ScriptManagerBridge.FrameCallback`) directly in the call stack —
    /// meaning that earlier conclusion was WRONG (or true only of an older
    /// Godot version): Godot DOES capture and use its own
    /// SynchronizationContext, and it resumes `await` continuations from
    /// the MAIN THREAD's own per-frame callback. That reconciles this hang
    /// exactly: every `await` in the old PrepareScrubProxiesAsync chain
    /// (this file, ScrubProxyCache, MediaProbe, FfmpegRunner — none of it
    /// ConfigureAwait(false), see (a) below) posted its continuation back
    /// through that captured context, so a CPU-heavy decode+encode pass
    /// ended up executing in chunks ON THE MAIN THREAD itself, between/
    /// within frame callbacks — visually indistinguishable from a genuine
    /// hang for as long as it took. FIX: proxy resolution no longer runs
    /// inline as part of session setup at all —
    /// KickOffScrubProxyResolution/KickOffProxyResolution fire each
    /// referenced source's resolution via `Task.Run(...)`, which
    /// unconditionally schedules onto the real ThreadPool regardless of any
    /// captured SynchronizationContext, and BuildScrubSessionAsync/
    /// ReverseVideoLoopAsync no longer await them at all — a scrub/reverse
    /// session is considered "ready" (EnsureScrubSessionBaseAsync returns,
    /// ReverseVideoLoopAsync proceeds) the moment its OWN GPU context
    /// exists, regardless of whether every referenced source's proxy has
    /// actually finished building yet. See MEDIA-OFFLINE PLACEHOLDER FOR
    /// NOT-YET-READY MEDIA below for what fills the gap in the meantime.
    ///
    /// ONE BACKGROUND BUILD PER DISTINCT SOURCE PATH, NOT PER NODE (fixed
    /// here, real-world regression found in testing directly after the fix
    /// above: "it attempted to build 7 identical files"): once proxy
    /// resolution moved onto a fire-and-forget Task.Run per VideoSourceNode
    /// (see directly above), a Timeline that references the SAME underlying
    /// source path from several distinct VideoSourceNode instances (the
    /// same footage cut into multiple clips, or reused across channels) fired
    /// one INDEPENDENT Task.Run per NODE, all racing to build that one
    /// source's proxy at once. ScrubProxyCache.GetOrBuildAsync's own
    /// in-flight-build coalescing (InFlight.GetOrAdd, keyed by content hash)
    /// looks like it should have caught this, but each caller must first
    /// `await MediaHasher.ComputeAsync(...)` (real, non-instant I/O) BEFORE
    /// it ever reaches that GetOrAdd call — several callers hashing the
    /// same file at once reliably finish within the same small window of
    /// each other, and .NET's ConcurrentDictionary.GetOrAdd does NOT
    /// guarantee its valueFactory runs only once under that kind of
    /// near-simultaneous contention: multiple racing calls can each invoke
    /// BuildAndTrackAsync (a real async method that starts running the
    /// instant it's invoked, not merely queued), with only one of the
    /// resulting Tasks actually kept in the dictionary — the others run to
    /// completion anyway, each against its own temp file, each ending in
    /// its own `File.Move(tempPath, finalPath, overwrite: true)` racing the
    /// others for the same final path. That is exactly "N identical builds"
    /// for one source, for N nodes that reference it. FIX, at the call
    /// site (the actual root cause — see also the cache-level hardening
    /// immediately below): KickOffScrubProxyResolution now groups every
    /// referenced VideoSourceNode by its own Source.Path FIRST, and
    /// KickOffProxyResolution fires exactly ONE Task.Run per DISTINCT path
    /// — its result is then written into `proxies` for every node Id that
    /// shares that path, once the one build/lookup completes. A Timeline
    /// with 7 clips cut from the same source file now resolves that source
    /// exactly once, not 7 times.
    ///
    /// ScrubProxyCache.GetOrBuildAsync ALSO HARDENED AGAINST THE SAME RACE,
    /// AS DEFENSE-IN-DEPTH (paired with the fix directly above): the
    /// call-site dedup above is the actual fix for THIS reported bug (one
    /// Timeline, resolved through Playback), but ScrubProxyCache is a
    /// process-wide static cache other call sites can reach too (e.g.
    /// PrewarmScrubProxiesAsync running concurrently with a live scrub
    /// session's own on-demand resolution for the same source) — so the
    /// underlying GetOrAdd race is fixed at the cache itself too, not just
    /// papered over here. See ScrubProxyCache's own remarks for the actual
    /// mechanism (wrapping the in-flight Task in a Lazy so only the winning
    /// caller's factory ever actually executes).
    ///
    /// MEDIA-OFFLINE PLACEHOLDER FOR NOT-YET-READY MEDIA (fixed here,
    /// paired with the fix directly above — direct user request: "the
    /// placeholder should just be used anytime any media comes up empty"):
    /// ScrubFrameSource now falls back to MediaPlaceholder.Get(...) — a
    /// small, cached, drawn-not-decoded "MEDIA OFFLINE" image — for ANY
    /// input node it can't currently resolve real content for: a video
    /// proxy that's still building in the background (see directly above),
    /// one whose build failed outright, a missing/corrupt static image, or
    /// a text node that fails to rasterize. See ScrubFrameSource's own
    /// class remarks, MEDIA-OFFLINE PLACEHOLDER, for the full per-case
    /// behavior. DELIBERATELY SCOPED TO SCRUB/REVERSE ONLY, NOT
    /// ClipContentSource, per direct user decision — forward Playback and
    /// Render/* keep throwing on broken/missing media; silently masking
    /// that in a real export or live playback could hide a real problem,
    /// whereas scrub/reverse is already an approximate preview by nature
    /// (see SCRUB/REVERSE VIA RAW SCRUB PROXIES above).
    ///
    /// ScrubProxyReady EVENT (new): once a background-kicked-off proxy
    /// build completes successfully, `proxies` (the SAME mutable
    /// dictionary ScrubFrameSource reads from) gains that source's entry,
    /// but nothing automatically re-renders the CURRENT scrub/reverse
    /// position with it — the next tick picks it up naturally
    /// (ScrubFrameSource reads `proxies` fresh every call), but until then
    /// whatever's already on screen (a placeholder, or a still-missing
    /// frame) keeps showing. ScrubProxyReady fires (from whatever
    /// background thread the build completed on — same "consumer marshals
    /// it themselves" convention as every other event on this class) so a
    /// consumer that wants to eagerly replace a placeholder can re-issue
    /// ScrubToAsync(Position) itself once notified; entirely optional,
    /// nothing internally depends on anyone handling it.
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
    /// OPTIMIZED-MEDIA CACHE (OptimizedMediaCache, EditSharp.Caching): a
    /// video clip's decoder in the FORWARD playback path may open against a
    /// persistent, content-addressed proxy instead of the clip's true
    /// original source file, whenever ContentPreparation.
    /// ProbeVideoAsync finds one already built and big enough. Scrubbing and
    /// reverse playback consult a COMPLETELY SEPARATE cache
    /// (ScrubProxyCache) instead — see the section above.
    ///
    /// SWITCHING BETWEEN SCRUB AND PLAYBACK (fixed, KEPT): Play() calls
    /// EndScrubbing() as its very first action, unconditionally, on BOTH
    /// the "resume an existing paused session" path and the "start a
    /// brand-new session" path, BEFORE taking _stateLock. Found in the
    /// field: scrubbing is explicitly allowed while playback is merely
    /// paused (ScrubToAsync's own guard is `State != PlaybackState.Playing`,
    /// not `State == PlaybackState.Inactive` — see STATE above) — Play()
    /// itself used to never tear an active scrub session down before
    /// (re)starting its own forward-playback session. Runs OUTSIDE
    /// `_stateLock` deliberately: EndScrubbing() blocks synchronously on
    /// `_scrubGate`, and ScrubToAsync can be holding `_scrubGate` while
    /// briefly needing `_stateLock` itself — calling EndScrubbing() while
    /// already holding `_stateLock` would risk a lock-order inversion
    /// deadlock. NOTE this fix now ONLY tears down `_scrubContentSource`
    /// (see SCRUB GPU CONTEXT IS NOW PERSISTENT below) — it no longer
    /// touches the scrub GpuContext at all, since that is now a persistent,
    /// once-created resource for this Playback instance's whole life.
    ///
    /// EAGER RELEASE ON PAUSE, REVERTED — TRIED, THEN EXPLICITLY UNDONE PER
    /// USER DIRECTION: a real GPU-contention concern was identified where
    /// scrubbing while paused stood up a scrub session's own GpuContext
    /// alongside a paused forward-playback session's still-alive one (two
    /// live GPU contexts at once). A first fix had each loop eagerly
    /// dispose its own GpuContext/SurfacePool/decoders the moment it
    /// noticed `pauseGate.IsPaused`, reacquiring only once actually
    /// resumed. That did NOT resolve the freezes still being reported, and
    /// it cost real playback smoothness — resuming from a pause stopped
    /// being free (it paid roughly a fresh Play()-setup cost every time),
    /// which the user explicitly called out as not worth it. REVERTED per
    /// direct instruction: VideoLoopAsync/ReverseVideoLoopAsync are back to
    /// a single GpuContext/SurfacePool/decoder set for the WHOLE forward-
    /// playback or reverse session, created once and torn down only at
    /// Stop()/natural end — pausing is, again, a cheap, instantly-resumable
    /// no-op for these resources UNLESS a scrub actually moved the position
    /// during the pause (see SCRUB DURING PAUSE FORCES A REAL SEEK ON
    /// RESUME below — that is a NEW, separate, narrowly-scoped mechanism,
    /// not a reintroduction of this reverted one: it only pays a restart
    /// cost when the position genuinely changed, never on every pause).
    /// SourceDecoder.Dispose()'s own WaitForExit-after-Kill fix (see that
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
    ///       blocking on it, and Godot's C# integration was believed at the
    ///       time to install NO SynchronizationContext at all — so this
    ///       theory was believed not to apply here, and the fix was treated
    ///       as a no-op for this app's real behavior. Removed for
    ///       cleanliness rather than left in as dead weight. SEE SCRUB
    ///       PROXY BUILDS RUN ON A REAL BACKGROUND THREAD, NEVER BLOCK
    ///       SESSION STARTUP above — later evidence shows the
    ///       "no SynchronizationContext at all" premise here was actually
    ///       WRONG; this revert's conclusion about ITS OWN theorized
    ///       deadlock risk is unaffected (a plain `.Wait()`-on-UI-thread
    ///       deadlock is a different mechanism than the main-thread-hang
    ///       bug that new evidence explains), but the "this app has no
    ///       SynchronizationContext, full stop" framing above it was never
    ///       actually true and should not be trusted at face value by a
    ///       future investigator.
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
    /// GPU WORK MUST STAY ON ONE THREAD — THE ACTUAL ROOT CAUSE OF THE
    /// EARLIER SCRUB LOCKUP (unrelated to the main-thread-hang fixed above,
    /// which is a separate bug with a separate mechanism — see SCRUB PROXY
    /// BUILDS RUN ON A REAL BACKGROUND THREAD above): regardless of whether
    /// Godot captures a SynchronizationContext (see (a) above for why that
    /// question turned out to matter, just not for THIS bug), EVERY `await`
    /// anywhere in this file could already land its continuation on a
    /// DIFFERENT OS thread than the call immediately before or after it —
    /// whether that continuation is scheduled by a captured
    /// SynchronizationContext's own queue or by the plain ThreadPool, the
    /// specific thread it actually runs on from one await to the next is
    /// not guaranteed to be the same one. That means every GPU-touching
    /// call this file makes (GpuContext.Create, SurfacePool's
    /// Rent/CreateSurface, FrameCompositor.RenderFrame, GpuContext.Dispose)
    /// could land on a different thread than the call before/after it,
    /// purely as an artifact of scheduling — true from the very first
    /// version of the scrub rewrite, independent of every fix tried above.
    /// Skia's GrDirectContext (GRContext in SkiaSharp) is not documented as
    /// safe for that usage pattern: sequential-but-cross-thread access to
    /// one GRContext, its SKSurfaces, and the D3D12 command queue backing
    /// it, with nothing pinning it to one thread, is a real, plausible
    /// source of a driver-level hang — more likely to actually manifest the
    /// more real GPU work a given call submits, which lines up with the
    /// reported pattern (a cheap/short first scrub tends to survive; a
    /// slower/longer one is more likely to get unlucky; once ANY frame has
    /// succeeded, later calls in the same process keep landing on threads
    /// already warmed up for this workload, which is why it then "stays
    /// fixed"). FIX: GpuThreadDispatcher (Composite/GpuThreadDispatcher.cs)
    /// confines every GPU-touching call for a given GpuContext/SurfacePool
    /// pair to ONE dedicated background thread, for as long as that pair is
    /// alive — used for the scrub GPU context/pool (`_scrubGpuThread`,
    /// persistent — see SCRUB GPU CONTEXT IS NOW PERSISTENT below) and for a
    /// session-scoped dispatcher inside both VideoLoopAsync and
    /// ReverseVideoLoopAsync (created and disposed alongside that session's
    /// own GpuContext/SurfacePool). Every PrefetchAsync/RenderFrame/
    /// Create/Dispose call for a given context now happens on that context's
    /// own single thread, every time, structurally — not by hoping the
    /// scheduler behaves favorably. CONFIRMED ON REAL HARDWARE — the
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
    /// SurfacePool sized for the FIRST scrub session's width/height would
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
    /// SCRUB DURING PAUSE FORCES A REAL SEEK ON RESUME (root cause of two
    /// reported bugs, both fixed): previously, a scrub taken while paused
    /// updated `_referenceClock`/Position (see directly above) but NOTHING
    /// ELSE — the video loop's persistent decode pipe (ClipContentSource/
    /// SourceDecoder, which can only move FORWARD) and the audio engine's
    /// own byte-offset pump both kept whatever position they were at before
    /// the pause, completely unaware a scrub had ever happened. A plain
    /// Play() resume just released the pause gate and resumed the wall
    /// clock in place, so BOTH streams simply continued from the PRE-scrub
    /// position — the scrub was a preview only, never an actual seek (bug
    /// 1: "scrubbing should change playback position, but it doesn't").
    /// Worse, the stale `_referenceClock` value left behind by the scrub
    /// caused a SEPARATE, visible symptom for a follower stream:
    /// PlaybackReferenceClock.Position resumes extrapolating forward, in
    /// real time, from wherever it was last Report()'d — if a scrub landed
    /// EARLIER than the actual pre-pause stopped position, that value
    /// starts BELOW the follower's own fixed target position, so the
    /// follower's `gap = target - referenceClock.Position` computes a large
    /// POSITIVE gap and sleeps (in one big Task.Delay, not a poll loop —
    /// see VideoLoopAsync's/PlaybackAudioEngine.PumpAsync's own wait logic)
    /// for approximately the real-time distance between the scrub position
    /// and the actual resume position, before the leader's own next report
    /// ever gets a chance to correct it — a real, reproducible multi-second
    /// stall between the leader (audio, in the default SyncToAudio mode)
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
    /// FIX: `_scrubGeneration` (bumped by ScrubToAsync under `_stateLock`)
    /// and `_scrubGenerationAtPause` (a snapshot of `_scrubGeneration` taken
    /// by Pause()) together detect "did an actual scrub happen since this
    /// pause started" — a plain int-equality check, immune to any timing
    /// race with the leader's own periodic Report() calls (which never
    /// touch `_scrubGeneration`). Play()'s resume branch (session already
    /// active and `startPosition == null`) now checks this first:
    ///   - NO scrub happened (generations match): unchanged, cheap,
    ///     instant in-place resume — `_pauseGate?.Resume()` +
    ///     `_referenceClock?.ResumeWallClock()` + `_state =
    ///     PlaybackState.Playing`, exactly as before this fix.
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
    /// SCRUB SETS POSITION FIRST, UNCONDITIONALLY (fixed here, a SEPARATE
    /// bug from SCRUB DURING PAUSE FORCES A REAL SEEK ON RESUME above):
    /// ScrubToAsync used to update `_lastKnownPosition`/`_referenceClock`/
    /// `_scrubGeneration` only AFTER a frame had successfully rendered and
    /// been delivered — meaning a scrub that failed to build/render for any
    /// reason (a proxy build error, a superseded/cancelled render, or even
    /// just calling ScrubToAsync before Play() has EVER been called once)
    /// left Position completely unmoved, silently. Position now updates as
    /// the FIRST thing ScrubToAsync does, right after validating the
    /// requested position is in range and BEFORE any of the session-setup/
    /// rendering machinery runs — a caller's scrub request is reflected in
    /// Position immediately and unconditionally, whether or not a visible
    /// frame ever actually renders for it. This also means Position now
    /// moves even on a session that has never played once — previously
    /// Position could only ever be driven by a successful render, which
    /// (see the gap below) tended to silently fail on a first-ever scrub
    /// against an empty proxy cache in some configurations.
    ///
    /// SCRUB FAILURES ARE NOW LOGGED, NOT SILENT (fixed here): ScrubToAsync
    /// is, in practice, a FIRE-AND-FORGET API for most real callers (the
    /// Godot consumer app calls it from a Slider.ValueChanged handler with
    /// no `await` at all) — before this fix, the only exception type this
    /// method ever caught was a superseded-request OperationCanceledException;
    /// any OTHER exception (a scrub-proxy build failure, a corrupt/missing
    /// source, anything thrown out of BuildScrubSessionAsync or the actual
    /// composite) propagated straight out of the async method as a faulted
    /// Task that a fire-and-forget caller never observes — completely
    /// silent, no log line, no visible symptom beyond "nothing rendered."
    /// This is very likely what was actually happening in the reported "an
    /// empty scrub proxy cache builds the containing folder but never the
    /// actual .esrp file, and scrubbing shows no video, with no error
    /// displayed" bug — BuildAsync creates the target directory before it
    /// ever calls EncodeAsync, so a failure partway through encoding leaves
    /// exactly that signature (folder exists, temp file cleaned up by
    /// BuildAsync's own catch, real exception never seen by anyone). FIX:
    /// ScrubToAsync now has a catch-all for any exception that ISN'T the
    /// caller's own cancellation, and logs it via
    /// EditSharpConfig.Logger.LogError with the full exception detail
    /// before swallowing it (deliberately not rethrown — there is no
    /// synchronous caller left by the time this runs to catch it, and an
    /// unobserved faulted Task's finalization can itself crash a process
    /// via TaskScheduler.UnobservedTaskException in some hosts, which
    /// swallowing here avoids). Position has already been set (see SCRUB
    /// SETS POSITION FIRST, UNCONDITIONALLY above) regardless of whether
    /// this catch ever fires, so a logged-and-swallowed failure here only
    /// means no new frame rendered for that request, not that Position
    /// silently failed to move too.
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
    ///      gone too. NOT YET CONFIRMED ON REAL HARDWARE.
    ///   8. CLOSED — see SCRUB SETS POSITION FIRST, UNCONDITIONALLY and
    ///      SCRUB FAILURES ARE NOW LOGGED, NOT SILENT above. NOT YET
    ///      CONFIRMED ON REAL HARDWARE.
    ///   9. CLOSED — see SCRUB PROXY BUILDS RUN ON A REAL BACKGROUND
    ///      THREAD, NEVER BLOCK SESSION STARTUP and MEDIA-OFFLINE
    ///      PLACEHOLDER FOR NOT-YET-READY MEDIA above. A scrub/reverse
    ///      session no longer blocks the calling thread while a missing
    ///      proxy builds; ScrubFrameSource shows the offline placeholder
    ///      for whatever isn't ready yet. REGRESSION FOUND ON REAL HARDWARE
    ///      ("attempted to build 7 identical files" / "No scrub proxy
    ///      registered" errors) AND FIXED — see ONE BACKGROUND BUILD PER
    ///      DISTINCT SOURCE PATH, NOT PER NODE above (the duplicate-build
    ///      race) and ScrubFrameSource's own MEDIA-OFFLINE PLACEHOLDER
    ///      remarks (the "No scrub proxy registered" throw is gone — that
    ///      path now falls back to the placeholder instead, which is what
    ///      should have shipped with this gap's original fix but did not
    ///      actually make it into the pushed file the first time). NOT YET
    ///      CONFIRMED ON REAL HARDWARE.
    ///  10. GPU DECODE FOR IndexedDelta7 — TRIED, THEN REMOVED PER
    ///      EXPLICIT USER CORRECTION: an earlier round of GPU work wired a
    ///      ScrubProxyGpuDecoder into every ScrubFrameSource this file
    ///      constructs (BuildScrubSessionAsync's persistent scrub session
    ///      and ReverseVideoLoopAsync's session-scoped one), attempting a
    ///      GPU decode of each IndexedDelta7 proxy frame before falling
    ///      back to IndexedDelta7Codec.Decode. The user clarified that
    ///      request was actually about the BUILD/ENCODE side, not decode —
    ///      a proxy read is already a cheap, single positioned file read
    ///      regardless of pixel format, so GPU-accelerating it bought
    ///      little, while the ENCODE side (see ScrubProxyCache/
    ///      ScrubProxyGpuEncoder) is the genuinely expensive, worth-
    ///      accelerating step. The decoder and its wiring here were
    ///      removed entirely as unneeded complexity; every scrub-proxy
    ///      frame this file resolves is read on the CPU now, exactly as it
    ///      was before that detour — see ScrubFrameSource's own remarks.
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

        /// <summary>
        /// This Playback instance's current coarse state — see
        /// PlaybackState's own remarks and this class's STATE section
        /// above for the full reasoning behind replacing the earlier
        /// IsPlaying/IsPaused boolean pair with this single enum.
        /// </summary>
        public PlaybackState State => _state;

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

        /// <summary>
        /// See class remarks, ScrubProxyReady EVENT — fires whenever a
        /// background-kicked-off scrub proxy build completes successfully.
        /// Purely a notification; nothing internally depends on anyone
        /// handling it, and firing it never touches session state itself.
        /// </summary>
        public event EventHandler? ScrubProxyReady;

        protected virtual void OnScrubProxyReady(EventArgs e)
        {
            ScrubProxyReady?.Invoke(this, e);
        }

        private readonly object _stateLock = new();

        /// <summary>
        /// See class remarks, STATE — the single source of truth for this
        /// instance's coarse playback state, replacing the earlier
        /// `_isPlaying` bool. Every place this class used to flip
        /// `_isPlaying` or read `_pauseGate?.IsPaused` now reads/writes
        /// this field instead, under `_stateLock` at every write.
        /// </summary>
        private PlaybackState _state = PlaybackState.Inactive;

        private CancellationTokenSource? _cts;
        private Task? _videoTask;
        private PlaybackAudioEngine? _audioEngine;
        private PlaybackPauseGate? _pauseGate;

        // See class remarks, SCRUB DURING PAUSE FORCES A REAL SEEK ON
        // RESUME. `_scrubGeneration` is bumped once per ScrubToAsync call
        // that gets far enough to validate its position (under
        // `_stateLock`, alongside its `_referenceClock.Report(position)`
        // call); `_scrubGenerationAtPause` is a snapshot of it taken by
        // Pause(). Play()'s resume branch compares the two to decide
        // whether an actual scrub — not just the leader's own periodic
        // position reports — happened since the session was paused.
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
        private SurfacePool? _scrubSurfacePool;

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
        // PERSISTENT). NOTE: completing no longer implies every referenced
        // source's proxy is ready — see SCRUB PROXY BUILDS RUN ON A REAL
        // BACKGROUND THREAD, NEVER BLOCK SESSION STARTUP.
        private Task? _scrubSetupTask;

        // The most recent ScrubToAsync call's own supersession token — see
        // class remarks, SCRUB COALESCING. Cancelled (and replaced) every
        // time a new ScrubToAsync call arrives, so an older, now-stale
        // request stops waiting/decoding promptly instead of queueing
        // behind _scrubGate.
        private CancellationTokenSource? _scrubSupersedeCts;

        /// <summary>True while `_state` is Playing or Paused — i.e. a play session currently exists, whether or not it's paused.</summary>
        private bool SessionActive => _state == PlaybackState.Playing || _state == PlaybackState.Paused;

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
                if (SessionActive && startPosition == null)
                {
                    if (_scrubGeneration == _scrubGenerationAtPause)
                    {
                        // No scrub happened since this pause started —
                        // same cheap, instant, in-place resume as always.
                        _pauseGate?.Resume();
                        _referenceClock?.ResumeWallClock();
                        _state = PlaybackState.Playing;
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

            if (SessionActive) Stop();

            lock (_stateLock)
            {
                if (SessionActive) return;

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

                _state = PlaybackState.Playing;
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
                if (_state != PlaybackState.Playing || _pauseGate == null) return;
                _pauseGate.Pause();
                _state = PlaybackState.Paused;
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
                if (!SessionActive) return;
                _state = PlaybackState.Inactive;
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
        /// missing on demand either way (in the BACKGROUND now, without
        /// blocking — see class remarks, SCRUB PROXY BUILDS RUN ON A REAL
        /// BACKGROUND THREAD). Safe to call at any time, including while a
        /// scrub session is already active or playback is running — it
        /// only ever reads/builds via ScrubProxyCache, it never touches
        /// this instance's own scrub-session state. UNLIKE that background
        /// path, THIS method is still fully awaited/blocking for ITS OWN
        /// caller — that is the entire point of calling it explicitly ahead
        /// of time.
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
        /// SETS Position (both `_lastKnownPosition` and, when a session is
        /// active/paused, the shared PlaybackReferenceClock), AND bumps
        /// `_scrubGeneration`, AS THE VERY FIRST THING THIS METHOD DOES —
        /// before session setup, before rendering, unconditionally — see
        /// class remarks, SCRUB SETS POSITION FIRST, UNCONDITIONALLY. Any
        /// failure in the render/session-setup work that follows is caught
        /// and logged rather than left to vanish into an unobserved Task —
        /// see class remarks, SCRUB FAILURES ARE NOW LOGGED, NOT SILENT.
        /// NEVER BLOCKS ON A MISSING SOURCE'S SCRUB PROXY BUILD ANY MORE —
        /// see class remarks, SCRUB PROXY BUILDS RUN ON A REAL BACKGROUND
        /// THREAD, NEVER BLOCK SESSION STARTUP; a node whose proxy isn't
        /// ready yet renders as the offline placeholder instead (see
        /// MEDIA-OFFLINE PLACEHOLDER FOR NOT-YET-READY MEDIA).
        ///
        /// SETS State TO Scrubbing FOR THE DURATION OF THE ACTUAL RENDER —
        /// see class remarks, SCRUB STATE IS NOW A REAL STATE, NOT AN
        /// ORTHOGONAL FLAG: whatever State was immediately before this call
        /// entered its gated section (Inactive, or Paused if a play session
        /// is paused) is saved and restored once the render finishes,
        /// succeeds or fails.
        /// </summary>
        public async Task ScrubToAsync(TimeSpan position, CancellationToken ct = default)
        {
            lock (_stateLock)
            {
                if (_state == PlaybackState.Playing)
                    throw new InvalidOperationException(
                        "ScrubToAsync cannot be used while actively playing — Pause() first.");
            }

            if (position < TimeSpan.Zero || position > Timeline.Duration)
                throw new ArgumentOutOfRangeException(nameof(position),
                    $"position must be within [0, {Timeline.Duration}].");

            // See class remarks, SCRUB SETS POSITION FIRST, UNCONDITIONALLY.
            // This is deliberately the very next thing that happens after
            // the two validation checks above — before the supersede/
            // cancellation plumbing, before session setup, before any
            // rendering — so Position always reflects the latest requested
            // scrub regardless of whether that scrub's own render ever
            // actually completes.
            lock (_stateLock)
            {
                _lastKnownPosition = position;
                _referenceClock?.Report(position);
                _scrubGeneration++;
            }

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

                // See class remarks, SCRUB STATE IS NOW A REAL STATE, NOT
                // AN ORTHOGONAL FLAG — save whatever State was right before
                // this scrub's own render work starts, so it can be
                // restored once this one render finishes. Safe without a
                // race: only one ScrubToAsync call is ever inside this
                // `_scrubGate`-held section at a time.
                PlaybackState stateBeforeScrub;
                lock (_stateLock)
                {
                    stateBeforeScrub = _state;
                    _state = PlaybackState.Scrubbing;
                }

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
                }
                finally
                {
                    lock (_stateLock) { _state = stateBeforeScrub; }
                    _scrubGate.Release();
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Superseded by a newer ScrubToAsync call — not the
                // caller's own cancellation, so nothing to propagate. See
                // class remarks, SCRUB COALESCING.
            }
            catch (OperationCanceledException)
            {
                // The caller's own cancellation (ct) — propagate normally,
                // same as always. Position was already set above regardless.
                throw;
            }
            catch (Exception ex)
            {
                // See class remarks, SCRUB FAILURES ARE NOW LOGGED, NOT
                // SILENT: most real callers never await this method, so an
                // exception that isn't logged here is never seen by
                // anyone. Deliberately swallowed rather than rethrown —
                // there is no synchronous caller left by now to catch it,
                // and letting a fire-and-forget Task end up faulted risks
                // an unobserved-exception crash in some hosts on
                // finalization. Position has already moved regardless of
                // this failure — only the rendered frame is missing.
                EditSharpConfig.Logger.LogError(
                    $"ScrubToAsync({position}) failed to render a frame: {ex}");
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
        /// just FrameCompositor.RenderFrame itself — runs as ONE unit of
        /// work on `gpuThread`, the dedicated thread that owns `pool`'s
        /// GpuContext. See class remarks, GPU WORK MUST STAY ON ONE
        /// THREAD. PrefetchAsync is blocked on synchronously
        /// (`.GetAwaiter().GetResult()`) INSIDE that unit of work rather
        /// than awaited — safe only because ScrubFrameSource's own remarks
        /// document it as fully synchronous under the hood (always returns
        /// Task.CompletedTask, and every proxy frame it reads is a plain
        /// CPU read — see its remarks, EVERY VIDEO-PROXY FRAME IS READ ON
        /// THE CPU), so this never actually blocks a thread on real I/O or
        /// attempts a reentrant dispatch onto `gpuThread` from within
        /// itself.
        /// </summary>
        private Task<(byte[] Buffer, int Length)> ComposeInstantFrameAsync(
            ScrubFrameSource contentSource, SurfacePool pool, GpuThreadDispatcher gpuThread,
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

                (byte[] buffer, int length) = FrameCompositor.RenderFrame(
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
        /// and the SurfacePool constructor both run on `_scrubGpuThread`
        /// itself (see class remarks, GPU WORK MUST STAY ON ONE THREAD), so
        /// this method's own await here is what actually makes that
        /// thread-confinement possible.
        ///
        /// NO LONGER AWAITS INDIVIDUAL SOURCE PROXY RESOLUTION AT ALL — see
        /// class remarks, SCRUB PROXY BUILDS RUN ON A REAL BACKGROUND
        /// THREAD, NEVER BLOCK SESSION STARTUP. KickOffScrubProxyResolution
        /// fires every referenced source's resolution on the ThreadPool and
        /// returns immediately; this method (and therefore
        /// EnsureScrubSessionBaseAsync/ScrubToAsync's own await on it)
        /// completes as soon as the GPU context/pool exist, regardless of
        /// whether any given source's proxy has actually finished building.
        ///
        /// `_scrubGpuContext`/`_scrubSurfacePool`/`_scrubGpuThread` back
        /// this session's own COMPOSITING (ComposeInstantFrameAsync's
        /// PrefetchAsync/RenderFrame calls) — the ScrubFrameSource this
        /// method constructs does NOT take them: it never touches the GPU
        /// itself, since every proxy frame it reads is a plain CPU read
        /// (see that class's own remarks).
        /// </summary>
        private async Task BuildScrubSessionAsync(int width, int height)
        {
            if (_scrubGpuContext == null)
            {
                _scrubGpuThread ??= new GpuThreadDispatcher("EditSharp-ScrubGPU");

                (GpuContext context, SurfacePool pool) = await _scrubGpuThread.RunAsync(() =>
                {
                    GpuContext ctx = GpuContext.Create(
                        RenderSettings.HardwareAccelerator, RenderSettings.GpuAdapterIndex);
                    var surfacePool = new SurfacePool(ctx.GRContext, width, height, Timeline.VideoChannels.Count);
                    return (ctx, surfacePool);
                });

                _scrubGpuContext = context;
                _scrubSurfacePool = pool;
            }

            var proxies = new ConcurrentDictionary<Guid, ScrubProxyEntry>();

            KickOffScrubProxyResolution(Timeline, RenderSettings.HardwareAccelerator, proxies);

            _scrubProxies = proxies;
            _scrubContentSource = new ScrubFrameSource(
                RenderSettings.Framerate, RenderSettings.HardwareAccelerator, proxies);
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
        /// Fires off resolution for every DISTINCT Video-type source path
        /// referenced by `timeline` — see class remarks, ONE BACKGROUND
        /// BUILD PER DISTINCT SOURCE PATH, NOT PER NODE — as an independent
        /// background task each (see KickOffProxyResolution) and returns
        /// IMMEDIATELY, without waiting for any of them. Deliberately NOT
        /// ContentPreparation.PrepareContentAsync, which probes
        /// native size/decode plan and consults OptimizedMediaCache — none
        /// of that applies here at all any more; this only ever needs each
        /// source's already-resolved-or-built ScrubProxyEntry.
        ///
        /// GROUPS VideoSourceNodes BY Source.Path FIRST (fixed here — see
        /// class remarks): several distinct nodes referencing the SAME
        /// source path collapse into ONE resolution/build for that path,
        /// whose result is then written into `proxies` for every one of
        /// those nodes' own Ids once it completes.
        /// </summary>
        private void KickOffScrubProxyResolution(
            Timeline timeline, HardwareAccelerator hwAccel,
            ConcurrentDictionary<Guid, ScrubProxyEntry> proxies)
        {
            var nodeIdsByPath = new Dictionary<string, List<Guid>>();

            foreach (VideoChannel channel in timeline.VideoChannels)
            {
                foreach (Clip clip in channel.Clips)
                {
                    if (clip is not VideoClip video) continue;

                    foreach (VideoSourceNode media in video.Graph.Nodes.OfType<VideoSourceNode>())
                    {
                        if (media.Source.Type != SourceType.Video) continue;

                        if (!nodeIdsByPath.TryGetValue(media.Source.Path, out List<Guid>? nodeIds))
                            nodeIdsByPath[media.Source.Path] = nodeIds = new List<Guid>();

                        nodeIds.Add(media.Id);
                    }
                }
            }

            foreach (KeyValuePair<string, List<Guid>> entry in nodeIdsByPath)
                KickOffProxyResolution(entry.Key, entry.Value, hwAccel, proxies);
        }

        /// <summary>
        /// Resolves (looking up, or building on a cache miss) ONE source
        /// path's scrub proxy via `Task.Run(...)` — a REAL, guaranteed
        /// ThreadPool hop regardless of any captured SynchronizationContext,
        /// since `ScrubProxyCache.GetOrBuildAsync`'s own await chain has
        /// none of its own ConfigureAwait(false) calls (see class remarks,
        /// SCRUB PROXY BUILDS RUN ON A REAL BACKGROUND THREAD). Called
        /// exactly ONCE per distinct source path (see
        /// KickOffScrubProxyResolution and class remarks, ONE BACKGROUND
        /// BUILD PER DISTINCT SOURCE PATH, NOT PER NODE) — the single
        /// resulting ScrubProxyEntry is written into `proxies` for EVERY
        /// node Id in `nodeIds` that shares this path, not just one.
        /// Fire-and-forget by design — the caller
        /// (KickOffScrubProxyResolution) doesn't wait for this, so failures
        /// are caught and logged HERE rather than left to fault an
        /// unobserved Task; `proxies` simply never gains an entry for any
        /// of these node Ids if the build fails, which ScrubFrameSource
        /// already treats as "show the offline placeholder for it" (see
        /// that class's own remarks) — permanently for this session, since
        /// nothing here retries a failed build on its own.
        /// </summary>
        private void KickOffProxyResolution(
            string sourcePath, IReadOnlyList<Guid> nodeIds, HardwareAccelerator hwAccel,
            ConcurrentDictionary<Guid, ScrubProxyEntry> proxies)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    ScrubProxyEntry resolvedEntry = await ScrubProxyCache.GetOrBuildAsync(sourcePath, hwAccel);
                    foreach (Guid nodeId in nodeIds) proxies[nodeId] = resolvedEntry;
                    OnScrubProxyReady(EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    EditSharpConfig.Logger.LogError(
                        $"Playback: failed to resolve a scrub proxy for '{sourcePath}': {ex}");
                }
            });
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
        /// GpuContext.Create, SurfacePool construction, the warm-up
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
        /// GpuContext/SurfacePool/decoders (contentSource) are `using`-
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
        /// resume on a different thread each time just like ScrubToAsync's
        /// could, since nothing about that scheduling is pinned to one
        /// thread by default — so without this, every per-frame
        /// FrameCompositor.RenderFrame call here carried the same
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
                    await ContentPreparation.PrepareContentAsync(
                        Timeline, width, height, RenderSettings.HardwareAccelerator,
                        nativeSizes, decodePlans, decodeSourcePaths);

                    Dictionary<Clip, TimeSpan> seekOffsets = ComputeSeekOffsets(Timeline, startPosition);

                    Dictionary<int, List<Clip>> decoderReleaseSchedule =
                        ContentPreparation.BuildDecoderReleaseSchedule(Timeline, fps);

                    using var contentSource = new ClipContentSource(
                        fps, RenderSettings.HardwareAccelerator, nativeSizes, decodePlans, seekOffsets, decodeSourcePaths);

                    using var videoGpuThread = new GpuThreadDispatcher("EditSharp-VideoGPU");

                    GpuContext gpuContext = await videoGpuThread.RunAsync(() =>
                        GpuContext.Create(RenderSettings.HardwareAccelerator, RenderSettings.GpuAdapterIndex));
                    SurfacePool surfacePool = await videoGpuThread.RunAsync(() =>
                        new SurfacePool(gpuContext.GRContext, width, height, Timeline.VideoChannels.Count));

                    try
                    {
                        int startFrame = (int)(startPosition.TotalSeconds * fps);
                        int totalFrames = Math.Max(1, (int)Math.Ceiling(Timeline.Duration.TotalSeconds * fps));

                        FrameState warmupState = FrameStateResolver.Resolve(Timeline, startFrame, fps);
                        (byte[] warmupBuffer, int warmupLength) = await videoGpuThread.RunAsync(() =>
                            FrameCompositor.RenderFrame(warmupState, contentSource, width, height, fps, surfacePool));
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
                                FrameCompositor.RenderFrame(state, contentSource, width, height, fps, surfacePool));

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
                lock (_stateLock) { _state = PlaybackState.Inactive; }
            }
        }

        /// <summary>
        /// REVERSE PLAYBACK (Speed &lt; 0): steps backward through the
        /// timeline, reusing the exact same instant, decoder-less raw scrub
        /// proxy read ScrubToAsync uses (ScrubFrameSource / ScrubProxyCache
        /// / ScrubProxyReader), NOT the persistent forward-only
        /// SourceDecoder pipe VideoLoopAsync uses, which structurally
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
        /// PROXY RESOLUTION NO LONGER BLOCKS THIS LOOP'S OWN STARTUP EITHER
        /// — see class remarks, SCRUB PROXY BUILDS RUN ON A REAL BACKGROUND
        /// THREAD: `proxies` is populated via the SAME
        /// KickOffScrubProxyResolution ScrubToAsync's own session setup
        /// uses (deduplicated by source path — see ONE BACKGROUND BUILD
        /// PER DISTINCT SOURCE PATH, NOT PER NODE), fired off and NOT
        /// awaited, so this loop's warm-up frame can render (via the
        /// offline placeholder for whatever isn't ready yet — see
        /// ScrubFrameSource's own remarks) without waiting on any source's
        /// build to finish first.
        ///
        /// GpuContext/SurfacePool/contentSource are session-scoped here
        /// too — same revert as VideoLoopAsync, see class remarks, EAGER
        /// RELEASE ON PAUSE, REVERTED. Uses its OWN session-scoped
        /// `reverseGpuThread` (NOT the persistent `_scrubGpuThread` scrub
        /// sessions use) — a reverse session already creates and tears down
        /// its own GpuContext/SurfacePool every time it starts/stops
        /// (unlike scrubbing, this wasn't changed to be persistent, since
        /// the user's request to preserve the GPU context was specifically
        /// about scrubbing), so a matching session-scoped dispatcher is the
        /// right shape here — see class remarks, GPU WORK MUST STAY ON ONE
        /// THREAD.
        ///
        /// `gpuContext`/`surfacePool`/`reverseGpuThread` back this
        /// session's own COMPOSITING (ComposeInstantFrameAsync's
        /// PrefetchAsync/RenderFrame calls) — the ScrubFrameSource this
        /// method constructs does NOT take them: it never touches the GPU
        /// itself, since every proxy frame it reads is a plain CPU read
        /// (see that class's own remarks).
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
                    using var reverseGpuThread = new GpuThreadDispatcher("EditSharp-ReverseGPU");

                    GpuContext gpuContext = await reverseGpuThread.RunAsync(() =>
                        GpuContext.Create(RenderSettings.HardwareAccelerator, RenderSettings.GpuAdapterIndex));
                    SurfacePool surfacePool = await reverseGpuThread.RunAsync(() =>
                        new SurfacePool(gpuContext.GRContext, width, height, Timeline.VideoChannels.Count));

                    var proxies = new ConcurrentDictionary<Guid, ScrubProxyEntry>();

                    KickOffScrubProxyResolution(Timeline, RenderSettings.HardwareAccelerator, proxies);

                    using var contentSource = new ScrubFrameSource(
                        fps, RenderSettings.HardwareAccelerator, proxies);

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
                lock (_stateLock) { _state = PlaybackState.Inactive; }
            }
        }

        private void TearDownAfterNaturalEnd()
        {
            CancellationTokenSource? cts;

            lock (_stateLock)
            {
                if (!SessionActive) return;

                _state = PlaybackState.Inactive;
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
        /// regardless of how many it has (see ClipContentSource.GetOrOpenDecoder).
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
        /// GpuContext/SurfacePool disposal still runs on
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