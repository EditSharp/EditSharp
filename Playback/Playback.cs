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
    /// deliberately, not by accident.
    ///
    /// SYNCHRONIZED STARTUP (PlaybackStartGate + AudioLeadTime): the video
    /// loop and the audio engine each need real setup time before either
    /// can start pacing itself, and that setup is asymmetric (video's is
    /// heavier — see PlaybackStartGate's own remarks). Both loops finish
    /// their own setup, signal the gate, and only THEN start their
    /// respective Stopwatch — this closed an observed video/audio desync
    /// (video visibly overtaking audio, timeline ending early) that traced
    /// back to the two loops' pacing clocks starting at different real
    /// wall-clock moments. AudioLeadTime, separately, lets audio deliver a
    /// burst of backlog to the consumer BEFORE that synchronized start —
    /// see PlaybackAudioEngine's own remarks for why that's safe and
    /// doesn't reintroduce a content-position offset.
    ///
    /// PAUSE VS STOP (PlaybackPauseGate): genuinely different operations.
    /// Pause() halts both loops in place via a resettable gate they check
    /// every iteration — decoders, the GpuContext/SkSurfacePool, and
    /// PlaybackAudioEngine's ffmpeg process all stay alive, ready to
    /// Resume() immediately. Stop() tears the whole session down via
    /// cancellation and is for when you're actually done — e.g. swapping
    /// Timeline out from under a session, which Pause() specifically does
    /// NOT support (everything paused stays keyed to the Timeline that was
    /// playing when Pause() was called).
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
    ///   4. FRAME-SKIPPING at Speed &gt; 1 is confirmed necessary,
    ///      especially at higher multiples (4x+) — v1 still renders and
    ///      calls SkSourceDecoder.NextFrame() for every frame in range and
    ///      only changes delivery cadence, which doesn't save real work at
    ///      high speeds. Needs a decode strategy that can actually skip
    ///      source frames, not just display them faster.
    ///   5. PlaybackMode is currently DECORATIVE — declared, defaults to
    ///      SyncToAudio, but nothing reads it yet. Actual behavior today
    ///      (synchronized-at-startup independent clocks, delay if behind,
    ///      never skip to catch up) is closest to EveryFrame regardless of
    ///      what the field is set to. Wiring real SyncToAudio (video pacing
    ///      off audio's actual delivered position rather than its own
    ///      Stopwatch at all) and FrameDropping (skip rather than delay
    ///      when behind) is future work.
    /// </summary>
    public class Playback : IDisposable
    {
        //timeline + settings describing what to play back and how
        public required Timeline Timeline;

        public required RenderSettings RenderSettings;

        //determines what aspect controls the pace of playback
        //NOTE: not yet wired to any actual behavior — see class remarks, gap 5
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
        //from outside deliberately, see class remarks
        public TimeSpan Position { get; private set; } = TimeSpan.Zero;

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

        public event EventHandler? PlaybackStarted;

        //raised exactly once per session, at the real moment video's clock
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

        public event EventHandler? EndReached;

        //raised when the end of the timeline is reached
        //(reverse playback / "beginning reached" isn't supported yet — see class remarks)
        protected virtual void OnEndReached(EventArgs e)
        {
            EndReached?.Invoke(this, e);
        }

        private readonly object _stateLock = new();
        private bool _isPlaying;
        private CancellationTokenSource? _cts;
        private Task? _videoTask;
        private PlaybackAudioEngine? _audioEngine;
        private PlaybackPauseGate? _pauseGate;

        /// <summary>
        /// Starts playback. startPosition, when given, is where playback
        /// BEGINS — a one-time parameter to this call, not a pre-set on
        /// Position. Omit it to resume from wherever Position last stopped.
        /// No-op if a session is already active (playing OR paused) — call
        /// Resume() to come back from a pause, not Play() again.
        /// </summary>
        public void Play(TimeSpan? startPosition = null)
        {
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

                Position = resolvedStart;

                _isPlaying = true;
                _cts = new CancellationTokenSource();
                CancellationToken token = _cts.Token;

                var pauseGate = new PlaybackPauseGate();
                _pauseGate = pauseGate;

                //audio v1: real-time only, see class remarks, gap 2
                bool audioParticipates = Math.Abs(Speed - 1f) < 0.0001f;

                //One shared gate so video's pacing clock and (if present)
                //audio's pacing clock both start at the SAME real moment.
                //"Ready" for audio now means "setup AND lead burst done" —
                //see PlaybackAudioEngine. onReleased fires PlaybackStarted
                //exactly once, right as the gate opens.
                var startGate = new PlaybackStartGate(
                    audioParticipates ? 2 : 1,
                    onReleased: () => OnPlaybackStarted(EventArgs.Empty));

                _videoTask = Task.Run(() => VideoLoopAsync(token, startGate, pauseGate), token);

                if (audioParticipates)
                {
                    var audioEngine = new PlaybackAudioEngine();
                    _audioEngine = audioEngine;

                    _ = audioEngine
                        .StartAsync(
                            Timeline, RenderSettings.Framerate,
                            (int)RenderSettings.Resolution.X, (int)RenderSettings.Resolution.Y,
                            Position, AudioLeadTime, startGate, pauseGate, args => OnAudioSample(args), token)
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
        /// ready to continue immediately via Resume(). Not the same
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
        /// Resumes a session previously halted by Pause(). No-op if not
        /// currently playing or not currently paused.
        /// </summary>
        public void Resume()
        {
            lock (_stateLock)
            {
                if (!_isPlaying || _pauseGate == null) return;
                _pauseGate.Resume();
            }

            EditSharpConfig.Logger.LogVerbose("Playback resumed.");
        }

        /// <summary>
        /// Fully tears the session down — cancels both loops, disposes the
        /// audio engine (which kills its ffmpeg process), and lets
        /// VideoLoopAsync's own finally block dispose its decoders/GPU
        /// context/surface pool. Use this when actually done with the
        /// session, e.g. before swapping Timeline out for a different one.
        /// For a temporary halt you intend to Resume() from, use Pause()
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

        private async Task VideoLoopAsync(CancellationToken token, PlaybackStartGate startGate, PlaybackPauseGate pauseGate)
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

                TimeSpan startPosition = Position;
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

                //Setup's done — wait for audio (if any) to also be ready
                //before starting the clock. Nothing between this line and
                //Stopwatch.StartNew() should do real work; that gap is
                //exactly what this gate exists to eliminate.
                await startGate.ReadyAndWaitAsync(token);
                var clock = Stopwatch.StartNew();
                EditSharpConfig.Logger.LogVerbose("Video pacing clock started.");

                for (int frameIndex = startFrame; frameIndex < totalFrames; frameIndex++)
                {
                    if (token.IsCancellationRequested) return;

                    //Pause check BEFORE rendering the next frame — nothing
                    //new gets produced while paused. Stopwatch.Stop()/
                    //Start() resumes elapsed-time accounting exactly where
                    //it left off rather than resetting, so the pacing math
                    //below needs no other change to account for a pause.
                    if (pauseGate.IsPaused)
                    {
                        clock.Stop();
                        try { await pauseGate.WaitIfPausedAsync(token); }
                        catch (OperationCanceledException) { return; }
                        clock.Start();
                    }

                    FrameState state = FrameStateResolver.Resolve(Timeline, frameIndex, fps, nativeSizes);

                    (byte[] buffer, int length) = SkFrameCompositor.RenderFrame(
                        state, contentSource, width, height, fps, surfacePool);

                    //Pacing: wait until wall-clock (scaled by Speed) reaches
                    //this frame's due time. Frame index still advances by
                    //exactly 1 every iteration regardless of Speed — real
                    //frame-skipping at Speed > 1 is gap 4, not implemented
                    //here yet.
                    TimeSpan targetElapsed = TimeSpan.FromSeconds(
                        (frameIndex - startFrame) / (double)fps / Speed);
                    TimeSpan actualElapsed = clock.Elapsed;

                    if (targetElapsed > actualElapsed)
                    {
                        try { await Task.Delay(targetElapsed - actualElapsed, token); }
                        catch (OperationCanceledException)
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                            return;
                        }
                    }

                    Position = startPosition + TimeSpan.FromSeconds((frameIndex - startFrame) / (double)fps);

                    try
                    {
                        OnVideoFrame(new VideoFrameEventArgs(buffer, length, width, height, Position));
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

                OnEndReached(EventArgs.Empty);
            }
            finally
            {
                foreach (string path in tempFiles)
                {
                    try { File.Delete(path); } catch { /* best-effort cleanup */ }
                }

                lock (_stateLock) { _isPlaying = false; }
            }
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
