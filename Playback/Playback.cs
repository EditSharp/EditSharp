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
using EditSharp.Components.Nodes;
using EditSharp.Components.Nodes.Sources;
using EditSharp.History;
using EditSharp.Audio;
using EditSharp.Audio.Engine;
using EditSharp.Components.Sources;
using EditSharp.Components.Sources.Video;
using EditSharp.Compositing;
using EditSharp.Compositing.Gpu;
using EditSharp.Compositing.Sources;
using EditSharp.Rendering;
using EditSharp.Video;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace EditSharp.Playback
{
    /// <summary>One clip's frame from <see cref="Playback.RenderClipFrameAsync"/>.</summary>
    /// <param name="Pixels">Tightly packed RGBA8888.</param>
    /// <param name="Width">The width in pixels.</param>
    /// <param name="Height">The height in pixels.</param>
    /// <param name="Complete">False when part of the clip showed a placeholder for something still on its way, such as a proxy not built that far yet; worth asking again later.</param>
    public sealed record ClipFrame(byte[] Pixels, int Width, int Height, bool Complete);

    /// <summary>Plays a timeline in real time, delivering frames and audio through events, and renders single frames for scrubbing and thumbnails.</summary>
    /// <remarks>
    /// <see cref="Play"/> starts a session: a video loop composites frames on its
    /// own GPU context and raises <see cref="VideoFrame"/>, and the audio engine
    /// raises <see cref="AudioSample"/>. <see cref="PlaybackMode"/> decides which
    /// of the two sets the pace. <see cref="Pause"/> holds a session with
    /// everything it has open; <see cref="Stop"/> ends it. A negative
    /// <see cref="Speed"/> plays in reverse, reading proxies behind the playhead.
    /// <para>
    /// <see cref="ScrubToAsync"/> shows one frame at any position while not
    /// playing; rapid calls cancel the ones before, so only the latest is
    /// delivered. Scrubbing and thumbnails share a second GPU context kept for
    /// this object's life. Sources are read as <see cref="RenderSettings"/>'s
    /// SourceMode says; nothing here builds proxies.
    /// </para>
    /// </remarks>
    public class Playback : IDisposable
    {
        /// <summary>The timeline to play.</summary>
        public required Timeline Timeline;

        /// <summary>The frame size, frame rate, GPU and source mode to play with.</summary>
        public required RenderSettings RenderSettings;

        /// <summary>What playback gives up when it can't keep up. Read when a session starts.</summary>
        public PlaybackMode PlaybackMode = PlaybackMode.SyncToAudio;

        /// <summary>Timeline seconds per second of real time: 1 is normal, 2 double speed, negative plays in reverse. Can't be 0. Read when a session starts.</summary>
        public float Speed = 1f;

        /// <summary>How audio keeps its pitch when <see cref="Speed"/> isn't 1, reverse included. Read when a session starts.</summary>
        public PitchPreservation PreservePitch = PitchPreservation.WSOLA;

        private readonly AudioTaps _audioTaps = new();

        /// <summary>Calls <paramref name="onBlock"/> with every block of audio passing a point in the mix.</summary>
        /// <remarks>The tap lasts across sessions until disposed. It's called on the audio thread, and the samples are only valid during the call.</remarks>
        /// <param name="id">An audio node's Id, a channel's Id, or <see cref="AudioTap.Master"/>.</param>
        /// <param name="onBlock">Receives each block.</param>
        /// <returns>Dispose it to remove the tap.</returns>
        public IDisposable TapAudio(Guid id, Action<AudioTapBlock> onBlock) => _audioTaps.Add(id, onBlock);

        private TimeSpan _lastKnownPosition = TimeSpan.Zero;
        private PlaybackReferenceClock? _referenceClock;
        /// <summary>Where playback is on the timeline: moving while playing, or the last position played or scrubbed to.</summary>
        public TimeSpan Position => _referenceClock?.Position ?? _lastKnownPosition;

        /// <summary>What playback is doing.</summary>
        public PlaybackState State => _state;

        /// <summary>Raised with each block of mixed audio while playing, on a background thread.</summary>
        public event EventHandler<AudioSampleEventArgs>? AudioSample;

        /// <summary>Raises <see cref="AudioSample"/>.</summary>
        /// <param name="e">The event's data.</param>
        protected virtual void OnAudioSample(AudioSampleEventArgs e)
        {
            AudioSample?.Invoke(this, e);
        }

        /// <summary>Raised with each frame shown, while playing or when a scrub finishes, on a background thread.</summary>
        public event EventHandler<VideoFrameEventArgs>? VideoFrame;

        /// <summary>Raises <see cref="VideoFrame"/>.</summary>
        /// <param name="e">The event's data.</param>
        protected virtual void OnVideoFrame(VideoFrameEventArgs e)
        {
            VideoFrame?.Invoke(this, e);
        }

        /// <summary>Raised when playback reaches the end of the timeline (its start, in reverse) and stops.</summary>
        public event EventHandler? EndReached;

        /// <summary>Raises <see cref="EndReached"/>.</summary>
        /// <param name="e">The event's data.</param>
        protected virtual void OnEndReached(EventArgs e)
        {
            EndReached?.Invoke(this, e);
        }

        /// <summary>Raised when a session has its sources ready and really starts moving.</summary>
        public event EventHandler? PlaybackStarted;

        /// <summary>Raises <see cref="PlaybackStarted"/>.</summary>
        /// <param name="e">The event's data.</param>
        protected virtual void OnPlaybackStarted(EventArgs e)
        {
            PlaybackStarted?.Invoke(this, e);
        }

        private readonly object _stateLock = new();

        //written under _stateLock
        private PlaybackState _state = PlaybackState.Inactive;

        private CancellationTokenSource? _cts;
        private Task? _videoTask;
        private PlaybackAudioEngine? _audioEngine;
        private PlaybackPauseGate? _pauseGate;

        //bumped by each scrub and snapshotted by Pause, so resuming knows whether a scrub moved the
        //position; the open decoders can't jump there, so the session restarts from it
        private int _scrubGeneration;
        private int _scrubGenerationAtPause;

        private readonly SemaphoreSlim _scrubGate = new(1, 1);

        //the scrub and thumbnail GPU context, made once and kept until Dispose; every call that
        //touches it runs on _scrubGpuThread, since a GPU context belongs to one thread
        private GpuThreadDispatcher? _scrubGpuThread;
        private GpuContext? _scrubGpuContext;
        private SurfacePool? _scrubSurfacePool;

        //random-access readers for the current scrub session; rebuilt after EndScrubbing
        private ClipContentSource? _scrubContentSource;

        //the scrub session's setup, started once and awaited by every scrub; cleared by EndScrubbing.
        //Finishing doesn't mean every proxy is ready: a missing one shows its placeholder
        private Task? _scrubSetupTask;

        //the latest scrub's token: a new scrub cancels it, so an older one stops instead of queueing
        private CancellationTokenSource? _scrubSupersedeCts;

        //a play session exists, paused or not
        private bool SessionActive => _state == PlaybackState.Playing || _state == PlaybackState.Paused;

        /// <summary>Starts playing, or resumes a paused session.</summary>
        /// <remarks>Resuming continues in place unless a scrub moved the position while paused; then the session restarts from there. Starting a new session stops any other and ends scrubbing.</remarks>
        /// <param name="startPosition">Where to start; null for <see cref="Position"/>. Ignored when resuming in place.</param>
        /// <exception cref="NotSupportedException"><see cref="Speed"/> is 0.</exception>
        /// <exception cref="ArgumentException">The timeline has no channels.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="startPosition"/> is outside the timeline.</exception>
        public void Play(TimeSpan? startPosition = null)
        {
            //before taking _stateLock, on both paths: a scrub allowed while paused would otherwise race the session
            EndScrubbing();

            lock (_stateLock)
            {
                if (SessionActive && startPosition == null)
                {
                    if (_scrubGeneration == _scrubGenerationAtPause)
                    {
                        //no scrub since the pause: resume in place
                        _pauseGate?.Resume();
                        _referenceClock?.ResumeWallClock();
                        _state = PlaybackState.Playing;
                        return;
                    }

                    //a scrub moved the position while paused and the open decoders can't jump there:
                    //restart from the scrubbed position
                    startPosition = _referenceClock?.Position;
                }
            }

            if (SessionActive) Stop();

            lock (_stateLock)
            {
                if (SessionActive) return;

                if (Speed == 0f)
                    throw new NotSupportedException(
                        "Playback.Speed cannot be 0; use Pause() or Stop(). Negative Speed plays in reverse.");

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

                //the clock runs at the playback speed: timeline time per second of wall time
                var referenceClock = new PlaybackReferenceClock(Speed);
                referenceClock.Report(resolvedStart);
                _referenceClock = referenceClock;

                //audio plays at every speed and in reverse; PlaybackMode decides who leads
                bool videoFollows = PlaybackMode == PlaybackMode.SyncToAudio;
                bool audioFollows = PlaybackMode is PlaybackMode.EveryFrame or PlaybackMode.FrameDropping;
                bool audioDropsLate = PlaybackMode == PlaybackMode.FrameDropping;

                var startGate = new PlaybackStartGate(2, onReleased: () =>
                {
                    referenceClock.Begin();
                    OnPlaybackStarted(EventArgs.Empty);
                });

                //wait for the previous session to let go of its GPU context, off the caller's thread,
                //and don't set anything up if this one is replaced meanwhile
                Task? previousVideoTask = _videoTask;

                _videoTask = Task.Run(async () =>
                {
                    await WaitForRetiredSessionAsync(previousVideoTask, token);

                    await (reverse
                        ? ReverseVideoLoopAsync(token, resolvedStart, startGate, pauseGate, referenceClock, videoFollows)
                        : VideoLoopAsync(token, resolvedStart, startGate, pauseGate, referenceClock, videoFollows));
                });

                var audioEngine = new PlaybackAudioEngine();
                _audioEngine = audioEngine;

                //off the caller's thread: preparing the start's sources is real work
                _ = Task.Run(() => audioEngine
                    .StartAsync(
                        Timeline, resolvedStart, Speed, PreservePitch, _audioTaps,
                        startGate, pauseGate, referenceClock, audioFollows, audioDropsLate,
                        args => OnAudioSample(args), token))
                    .ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                            EditSharpConfig.Logger.Log($"Playback audio engine failed to start: {t.Exception}");
                    }, TaskScheduler.Default);
            }
        }

        /// <summary>Pauses a playing session, keeping everything it has open.</summary>
        public void Pause()
        {
            lock (_stateLock)
            {
                if (_state != PlaybackState.Playing || _pauseGate == null) return;
                _pauseGate.Pause();
                _state = PlaybackState.Paused;
                //so resuming can tell whether a scrub happened meanwhile
                _scrubGenerationAtPause = _scrubGeneration;
                _referenceClock?.PauseWallClock();
            }

            EditSharpConfig.Logger.LogVerbose("Playback paused.");
        }

        /// <summary>Ends the session. Position stays where it stopped.</summary>
        /// <remarks>Returns without waiting; the session's GPU context and decoders are released on its own thread.</remarks>
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

            //cancelled, not waited on: the next Play (or Dispose) waits for it off the caller's thread.
            //The source isn't disposed, since the retiring session may still read its token
            cts?.Cancel();

            _audioEngine?.Dispose();
            _audioEngine = null;
        }

        //waits for a retired session to release its GPU context and decoders, unless `token` cancels first
        private static async Task WaitForRetiredSessionAsync(Task? previous, CancellationToken token)
        {
            if (previous != null)
            {
                try { await previous.WaitAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { /* the retired session's outcome isn't this one's concern */ }
            }

            token.ThrowIfCancellationRequested();
        }

        //whether `token` is the current session's; false once Stop or a newer Play replaced it
        private bool IsCurrentSession(CancellationToken token) => _cts != null && _cts.Token == token;

        /// <summary>Shows the frame at a position, delivered through <see cref="VideoFrame"/>.</summary>
        /// <remarks>
        /// Safe to call on every pointer move: each call cancels any unfinished one,
        /// and a cancelled call completes without an exception. <see cref="Position"/>
        /// moves at once, even if the frame never arrives. Failures are logged, not
        /// thrown. A source without a proxy yet shows its placeholder. State is
        /// Scrubbing while the frame is composed.
        /// </remarks>
        /// <param name="position">The timeline time to show.</param>
        /// <param name="ct">Cancels the scrub; unlike being superseded, this throws.</param>
        /// <exception cref="InvalidOperationException">Called while playing.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is outside the timeline.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
        public async Task ScrubToAsync(TimeSpan position, CancellationToken ct = default)
        {
            lock (_stateLock)
            {
                if (_state == PlaybackState.Playing)
                    throw new InvalidOperationException(
                        "ScrubToAsync cannot be used while playing; Pause() first.");
            }

            if (position < TimeSpan.Zero || position > Timeline.Duration)
                throw new ArgumentOutOfRangeException(nameof(position),
                    $"position must be within [0, {Timeline.Duration}].");

            //position first, so it's right even if this render never finishes
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
                //ConfigureAwait(false) while holding the gate: resuming on a UI thread's context could deadlock
                //against that thread waiting on the gate
                await _scrubGate.WaitAsync(linked).ConfigureAwait(false);

                //restored when this render ends; only one scrub is inside the gate at a time
                PlaybackState stateBeforeScrub;
                lock (_stateLock)
                {
                    stateBeforeScrub = _state;
                    _state = PlaybackState.Scrubbing;
                }

                try
                {
                    //cancels only this wait, never the shared setup
                    await EnsureScrubSessionBaseAsync(width, height).WaitAsync(linked).ConfigureAwait(false);

                    //the setup above always creates _scrubGpuThread
                    (byte[] buffer, int length) = await ComposeInstantFrameAsync(
                        _scrubContentSource!, _scrubSurfacePool!, _scrubGpuThread!,
                        position, width, height, fps, linked).ConfigureAwait(false);

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
                //replaced by a newer scrub, not cancelled by the caller: nothing to report
            }
            catch (OperationCanceledException)
            {
                //the caller cancelled: rethrow. Position has already moved
                throw;
            }
            catch (Exception ex)
            {
                //logged, not thrown: most callers never await this, so an exception here would go unseen
                //and could crash some hosts as unobserved. Position has already moved
                EditSharpConfig.Logger.LogError(
                    $"ScrubToAsync({position}) failed to render a frame: {ex}");
            }
        }

        /// <summary>
        /// One frame at an arbitrary position, for scrubbing: prepares the
        /// sources of every clip visible there (random-access readers, so any
        /// position in any order), then composes it on the scrub GPU thread.
        /// </summary>
        private async Task<(byte[] Buffer, int Length)> ComposeInstantFrameAsync(
            ClipContentSource contentSource, SurfacePool pool, GpuThreadDispatcher gpuThread,
            TimeSpan position, int width, int height, int fps, CancellationToken ct = default)
        {
            int frameIndex = (int)(position.TotalSeconds * fps);
            FrameState state = FrameStateResolver.Resolve(Timeline, frameIndex, fps);

            await contentSource.PrepareAsync(state, ct).ConfigureAwait(false);

            return await gpuThread.RunAsync(() =>
                FrameCompositor.RenderFrame(state, contentSource, width, height, fps, pool)).ConfigureAwait(false);
        }

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

        private Task EnsureScrubSessionBaseAsync(int width, int height) =>
            _scrubSetupTask ??= BuildScrubSessionAsync(width, height);

        //the scrub GPU context is made once per Playback; the content source is rebuilt every session.
        //Nothing builds proxies: a source without one shows ProxyMissing or ProxyPending
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
                }).ConfigureAwait(false);

                _scrubGpuContext = context;
                _scrubSurfacePool = pool;
            }

            _scrubContentSource = new ClipContentSource(new ContentSourceOptions(
                RenderSettings.Framerate, width, height, RenderSettings.HardwareAccelerator, RenderSettings.SourceMode,
                VideoReadMode.RandomAccess, ContentFailurePolicy.Preview));
        }

        /// <summary>One clip composited on its own, for thumbnails.</summary>
        /// <remarks>Media is read once, in RenderSettings.SourceMode, so nothing stays open between calls.</remarks>
        /// <param name="clip">The clip.</param>
        /// <param name="contentTime">The clip's content time to show.</param>
        /// <param name="width">The frame's width in pixels.</param>
        /// <param name="height">The frame's height in pixels.</param>
        /// <param name="ct">Cancels the render.</param>
        /// <returns>The frame, and whether any part of it is still to come.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="clip"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> isn't positive.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
        public async Task<ClipFrame> RenderClipFrameAsync(
            VideoClip clip, TimeSpan contentTime, int width, int height, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(clip);
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "width and height must both be positive.");

            int canvasWidth = (int)RenderSettings.Resolution.X;
            int canvasHeight = (int)RenderSettings.Resolution.Y;
            int fps = RenderSettings.Framerate;
            double clipSeconds = Math.Max(0d, contentTime.TotalSeconds);

            await _scrubGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await EnsureScrubSessionBaseAsync(canvasWidth, canvasHeight).WaitAsync(ct).ConfigureAwait(false);

                ClipContentSource source = _scrubContentSource!;
                SurfacePool pool = _scrubSurfacePool!;

                Graph graph;
                using (ModelLock.Read()) graph = clip.Graph.Snapshot();

                (IReadOnlyDictionary<Guid, (SKImage Image, bool Transient)> media, bool mediaComplete) =
                    await source.GetMediaFramesOnceAsync(graph, clipSeconds, width, height, ct).ConfigureAwait(false);

                return await _scrubGpuThread!.RunAsync(() =>
                {
                    IReadOnlyDictionary<Guid, (SKImage Image, bool Transient)> resolved =
                        source.GetContent(clip, graph, clipSeconds, 0, width, height, pool, media);
                    bool complete = mediaComplete && !source.LastFrameIncomplete;

                    var plain = new Dictionary<Guid, SKImage>(resolved.Count);
                    foreach (KeyValuePair<Guid, (SKImage Image, bool Transient)> entry in resolved)
                        plain[entry.Key] = entry.Value.Image;

                    try
                    {
                        var context = new SkClipChainContext(width, height, fps, 1.0 / fps);
                        SKSurface surface = pool.Rent(width, height);

                        try
                        {
                            surface.Canvas.Clear(SKColors.Black);
                            ClipCompositor.Composite(surface.Canvas, graph, plain, clipSeconds, context, pool);

                            using SKImage image = surface.Snapshot();
                            return new ClipFrame(ReadPixels(image, width, height), width, height, complete);
                        }
                        finally
                        {
                            pool.Return(surface, width, height);
                        }
                    }
                    finally
                    {
                        foreach (KeyValuePair<Guid, (SKImage Image, bool Transient)> entry in resolved)
                        {
                            if (entry.Value.Transient) entry.Value.Image.Dispose();
                        }
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                _scrubGate.Release();
            }
        }

        private static byte[] ReadPixels(SKImage image, int width, int height)
        {
            byte[] pixels = new byte[width * height * 4];
            GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);

            try
            {
                var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

                if (!image.ReadPixels(info, handle.AddrOfPinnedObject(), width * 4))
                    throw new InvalidOperationException("Failed to read the rendered clip frame's pixels.");
            }
            finally
            {
                handle.Free();
            }

            return pixels;
        }

        /// <summary>Ends a scrub session, closing its readers; the next scrub starts a new one.</summary>
        /// <remarks><see cref="Play"/> calls it. The scrub GPU context is kept.</remarks>
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
            }
            finally
            {
                _scrubGate.Release();
            }
        }

        /// <summary>
        /// Forward playback. Every source is read ahead: the content source
        /// prepares each clip's media and starts buffering it before the clip
        /// reaches the playhead (see ClipContentSource.Anticipate). What happens
        /// when a frame still isn't ready in time depends on PlaybackMode:
        ///   EveryFrame     video leads its own clock and waits for every frame; audio follows.
        ///   SyncToAudio    video follows the audio clock; a frame not ready by the time the
        ///                  next one is due is skipped, jumping to the frame due now.
        ///   FrameDropping  the wall clock leads: video reports it and skips late frames the
        ///                  same way; audio follows it and drops chunks that are already late.
        /// A skipped frame isn't emitted; the previous one stays on screen.
        /// </summary>
        private async Task VideoLoopAsync(
            CancellationToken token, TimeSpan startPosition,
            PlaybackStartGate startGate, PlaybackPauseGate pauseGate,
            PlaybackReferenceClock referenceClock, bool followsReferenceClock)
        {
            int width = (int)RenderSettings.Resolution.X;
            int height = (int)RenderSettings.Resolution.Y;
            int fps = RenderSettings.Framerate;
            PlaybackMode mode = PlaybackMode;

            try
            {
                try
                {
                    using var contentSource = new ClipContentSource(new ContentSourceOptions(
                        fps, width, height, RenderSettings.HardwareAccelerator, RenderSettings.SourceMode,
                        VideoReadMode.Sequential, ContentFailurePolicy.Preview, Buffered: true, Direction: 1));

                    int startFrame = (int)(startPosition.TotalSeconds * fps);
                    int totalFrames = Math.Max(1, (int)Math.Ceiling(Timeline.Duration.TotalSeconds * fps));

                    FrameState warmupState = FrameStateResolver.Resolve(Timeline, startFrame, fps);
                    await contentSource.PrepareAsync(warmupState, token);
                    contentSource.Anticipate(Timeline, startFrame);

                    using var videoGpuThread = new GpuThreadDispatcher("EditSharp-VideoGPU");

                    GpuContext gpuContext = await videoGpuThread.RunAsync(() =>
                        GpuContext.Create(RenderSettings.HardwareAccelerator, RenderSettings.GpuAdapterIndex));
                    SurfacePool surfacePool = await videoGpuThread.RunAsync(() =>
                        new SurfacePool(gpuContext.GRContext, width, height, Timeline.VideoChannels.Count));

                    try
                    {
                        token.ThrowIfCancellationRequested();

                        //the first frame always waits: there's nothing on screen yet to keep showing
                        await Task.Run(() => contentSource.WaitReady(warmupState, Timeout.InfiniteTimeSpan), token);
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

                        //where the playhead really is right now, for the modes that can fall behind
                        TimeSpan Now() => followsReferenceClock
                            ? referenceClock.Position
                            : startPosition + TimeSpan.FromSeconds(clock!.Elapsed.TotalSeconds * Speed);

                        int skipped = 0;

                        for (int frameIndex = startFrame + 1; frameIndex < totalFrames; frameIndex++)
                        {
                            TimeSpan framePosition = FrameStateResolver.TimeOfFrame(frameIndex, fps);

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

                            TimeSpan readyWait = Timeout.InfiniteTimeSpan;

                            if (mode != PlaybackMode.EveryFrame)
                            {
                                //already behind: go straight to the frame that's due now
                                int due = Math.Min(totalFrames - 1, (int)(Now().TotalSeconds * fps));
                                if (due > frameIndex)
                                {
                                    skipped += due - frameIndex;
                                    frameIndex = due;
                                    framePosition = FrameStateResolver.TimeOfFrame(frameIndex, fps);
                                }

                                //this frame may take until the next one is due, and no longer
                                TimeSpan untilNext = FrameStateResolver.TimeOfFrame(frameIndex + 1, fps) - Now();
                                readyWait = Max(TimeSpan.Zero, TimeSpan.FromSeconds(untilNext.TotalSeconds / Math.Max(Speed, 0.0001f)));
                            }

                            contentSource.Anticipate(Timeline, frameIndex);
                            FrameState state = FrameStateResolver.Resolve(Timeline, frameIndex, fps);

                            bool ready = await Task.Run(() => contentSource.WaitReady(state, readyWait), token);

                            if (mode == PlaybackMode.FrameDropping && !followsReferenceClock)
                                referenceClock.Report(Now());

                            if (!ready)
                            {
                                skipped++;
                                continue;
                            }

                            (byte[] buffer, int length) = await videoGpuThread.RunAsync(() =>
                                FrameCompositor.RenderFrame(state, contentSource, width, height, fps, surfacePool));

                            if (!followsReferenceClock)
                            {
                                TimeSpan targetElapsed = TimeSpan.FromSeconds((framePosition - startPosition).TotalSeconds / Speed);
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
                        }

                        if (skipped > 0)
                            EditSharpConfig.Logger.LogVerbose($"Playback skipped {skipped} frame(s) that weren't ready in time.");

                        TearDownAfterNaturalEnd(token);

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
                    startGate.Fault(ex);
                    throw;
                }
            }
            finally
            {
                lock (_stateLock) { if (IsCurrentSession(token)) _state = PlaybackState.Inactive; }
            }
        }

        /// <summary>
        /// Reverse playback. Reads are random-access (the proxy), buffered
        /// BEHIND the playhead. PlaybackMode works as it does forwards: in
        /// SyncToAudio video follows the audio clock, otherwise it leads on its
        /// own clock; EveryFrame waits for every frame, the other modes skip a
        /// frame that isn't ready in time and jump to the frame due now.
        /// </summary>
        private async Task ReverseVideoLoopAsync(
            CancellationToken token, TimeSpan startPosition,
            PlaybackStartGate startGate, PlaybackPauseGate pauseGate,
            PlaybackReferenceClock referenceClock, bool followsReferenceClock)
        {
            int width = (int)RenderSettings.Resolution.X;
            int height = (int)RenderSettings.Resolution.Y;
            int fps = RenderSettings.Framerate;
            double speedMagnitude = Math.Abs(Speed);
            PlaybackMode mode = PlaybackMode;

            try
            {
                try
                {
                    using var contentSource = new ClipContentSource(new ContentSourceOptions(
                        fps, width, height, RenderSettings.HardwareAccelerator, RenderSettings.SourceMode,
                        VideoReadMode.RandomAccess, ContentFailurePolicy.Preview, Buffered: true, Direction: -1));

                    int startFrame = (int)(startPosition.TotalSeconds * fps);

                    FrameState warmupState = FrameStateResolver.Resolve(Timeline, startFrame, fps);
                    await contentSource.PrepareAsync(warmupState, token);
                    contentSource.Anticipate(Timeline, startFrame);

                    using var reverseGpuThread = new GpuThreadDispatcher("EditSharp-ReverseGPU");

                    GpuContext gpuContext = await reverseGpuThread.RunAsync(() =>
                        GpuContext.Create(RenderSettings.HardwareAccelerator, RenderSettings.GpuAdapterIndex));
                    SurfacePool surfacePool = await reverseGpuThread.RunAsync(() =>
                        new SurfacePool(gpuContext.GRContext, width, height, Timeline.VideoChannels.Count));

                    try
                    {
                        token.ThrowIfCancellationRequested();

                        await Task.Run(() => contentSource.WaitReady(warmupState, Timeout.InfiniteTimeSpan), token);
                        (byte[] warmupBuffer, int warmupLength) = await reverseGpuThread.RunAsync(() =>
                            FrameCompositor.RenderFrame(warmupState, contentSource, width, height, fps, surfacePool));
                        EditSharpConfig.Logger.LogVerbose("Reverse video warm-up frame rendered.");

                        await startGate.ReadyAndWaitAsync(token);

                        Stopwatch? clock = followsReferenceClock ? null : Stopwatch.StartNew();
                        EditSharpConfig.Logger.LogVerbose(followsReferenceClock
                            ? "Reverse video now following the reference clock."
                            : "Reverse video pacing clock started.");

                        if (!followsReferenceClock) referenceClock.Report(startPosition);
                        try
                        {
                            OnVideoFrame(new VideoFrameEventArgs(warmupBuffer, warmupLength, width, height, startPosition));
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(warmupBuffer);
                        }

                        int skipped = 0;

                        for (int frameIndex = startFrame - 1; frameIndex >= 0; frameIndex--)
                        {
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

                                //following: wait for the (descending) clock to reach this frame
                                if (followsReferenceClock)
                                {
                                    TimeSpan gap = referenceClock.Position - FrameStateResolver.TimeOfFrame(frameIndex, fps);

                                    if (gap > TimeSpan.Zero)
                                    {
                                        TimeSpan wait = TimeSpan.FromTicks((long)(gap.Ticks / speedMagnitude));
                                        try { await Task.Delay(Max(wait, PlaybackReferenceClock.PollInterval), token); }
                                        catch (OperationCanceledException) { return; }
                                        continue;
                                    }
                                }

                                break;
                            }

                            //where the playhead really is right now
                            TimeSpan Now() => followsReferenceClock
                                ? referenceClock.Position
                                : startPosition - TimeSpan.FromSeconds(clock!.Elapsed.TotalSeconds * speedMagnitude);

                            TimeSpan readyWait = Timeout.InfiniteTimeSpan;

                            if (mode != PlaybackMode.EveryFrame)
                            {
                                int due = Math.Max(0, (int)Math.Ceiling(Now().TotalSeconds * fps));
                                if (due < frameIndex)
                                {
                                    skipped += frameIndex - due;
                                    frameIndex = due;
                                }

                                TimeSpan untilNext = Now() - FrameStateResolver.TimeOfFrame(frameIndex - 1, fps);
                                readyWait = Max(TimeSpan.Zero, TimeSpan.FromTicks((long)(untilNext.Ticks / speedMagnitude)));
                            }

                            TimeSpan framePosition = FrameStateResolver.TimeOfFrame(frameIndex, fps);

                            contentSource.Anticipate(Timeline, frameIndex);
                            FrameState state = FrameStateResolver.Resolve(Timeline, frameIndex, fps);

                            if (!await Task.Run(() => contentSource.WaitReady(state, readyWait), token))
                            {
                                skipped++;
                                continue;
                            }

                            if (!followsReferenceClock)
                            {
                                TimeSpan targetElapsed = TimeSpan.FromSeconds((startFrame - frameIndex) / (fps * speedMagnitude));
                                TimeSpan actualElapsed = clock!.Elapsed;

                                if (targetElapsed > actualElapsed)
                                {
                                    try { await Task.Delay(targetElapsed - actualElapsed, token); }
                                    catch (OperationCanceledException) { return; }
                                }
                            }

                            (byte[] buffer, int length) = await reverseGpuThread.RunAsync(() =>
                                FrameCompositor.RenderFrame(state, contentSource, width, height, fps, surfacePool));

                            if (!followsReferenceClock) referenceClock.Report(framePosition);

                            try
                            {
                                OnVideoFrame(new VideoFrameEventArgs(buffer, length, width, height, framePosition));
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(buffer);
                            }
                        }

                        if (skipped > 0)
                            EditSharpConfig.Logger.LogVerbose($"Reverse playback skipped {skipped} frame(s) that weren't ready in time.");

                        TearDownAfterNaturalEnd(token);

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
                lock (_stateLock) { if (IsCurrentSession(token)) _state = PlaybackState.Inactive; }
            }
        }

        private void TearDownAfterNaturalEnd(CancellationToken token)
        {
            lock (_stateLock)
            {
                //a replaced session reaching its end must not tear down the one that replaced it
                if (!SessionActive || !IsCurrentSession(token)) return;

                _state = PlaybackState.Inactive;
                _lastKnownPosition = _referenceClock?.Position ?? _lastKnownPosition;
                _referenceClock = null;
                _pauseGate = null;
                _cts = null;
            }

            _audioEngine?.Dispose();
            _audioEngine = null;
        }

        /// <summary>Stops playback and releases everything, the scrub GPU context included.</summary>
        /// <remarks>Waits for the session to release its GPU context and decoders before returning.</remarks>
        public void Dispose()
        {
            Stop();

            //the one place a retired session is waited on synchronously
            try { _videoTask?.GetAwaiter().GetResult(); }
            catch (Exception) { /* cancelled or failed: either way, finished */ }
            _videoTask = null;

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