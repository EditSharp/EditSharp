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
    /// CONFIG: takes a Blueprint rather than a bare Timeline, deliberately —
    /// resolution/framerate/hardware accelerator are exactly the same
    /// concept for playback as for a full render, so Playback doesn't carry
    /// its own duplicate copies of them (an earlier pass here added
    /// Width/Height/Fps/HardwareAccelerator fields directly on Playback;
    /// corrected to reuse Blueprint instead). Blueprint.OutputDirectory,
    /// VideoCodec, and AudioCodec are unused by playback but still required
    /// to construct one, since Blueprint doesn't currently distinguish
    /// "render target config" from "output file config" — flagged as an
    /// awkward-but-accepted consequence of reuse, not fixed here.
    ///
    /// Position IS DELIBERATELY READ-ONLY FROM OUTSIDE. It exists for a
    /// consumer to ask "where is playback actually at right now," not as an
    /// input — a public setter would mean the video loop has to keep
    /// re-checking whether something external changed it out from under a
    /// running session. Starting from a nonzero position is a PARAMETER TO
    /// Play(), not a pre-set on Position — see Play(TimeSpan?) below.
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
    /// </summary>
    public class Playback : IDisposable
    {
        //blueprint describing what to play back and how — see class remarks
        //for why this replaced a bare Timeline plus duplicate config fields
        public required Blueprint Blueprint;

        //speed at which to play back the timeline
        //NOTE: only positive values are currently supported — see class
        //remarks, gap 1. A negative or zero Speed throws from Play(), not
        //silently clamped. Values != 1 currently play video only — gap 2.
        public float Speed = 1f;

        //how far along the playback is through the timeline — READ ONLY
        //from outside deliberately, see class remarks
        public TimeSpan Position { get; private set; } = TimeSpan.Zero;

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

        private readonly object _stateLock = new();
        private bool _isPlaying;
        private CancellationTokenSource? _cts;
        private Task? _videoTask;
        private PlaybackAudioEngine? _audioEngine;

        /// <summary>
        /// Starts playback. startPosition, when given, is where playback
        /// BEGINS — a one-time parameter to this call, not a pre-set on
        /// Position (Position only ever reflects where the engine actually
        /// is, updated internally as frames are delivered). Omit it to
        /// resume from wherever Position last stopped.
        /// </summary>
        public void Play(TimeSpan? startPosition = null)
        {
            lock (_stateLock)
            {
                if (_isPlaying) return;

                Timeline timeline = Blueprint.Timeline;

                if (Speed <= 0f)
                    throw new NotSupportedException(
                        "Playback.Speed <= 0 (reverse or stopped-via-speed) is not supported yet " +
                        "— see Playback's class remarks, gap 1.");

                if (timeline.Channels.Count == 0)
                    throw new ArgumentException("Blueprint.Timeline must contain at least one Channel.");

                TimeSpan resolvedStart = startPosition ?? Position;

                if (resolvedStart < TimeSpan.Zero || resolvedStart > timeline.Duration)
                    throw new ArgumentOutOfRangeException(nameof(startPosition),
                        $"startPosition must be within [0, {timeline.Duration}].");

                Position = resolvedStart;

                _isPlaying = true;
                _cts = new CancellationTokenSource();
                CancellationToken token = _cts.Token;

                _videoTask = Task.Run(() => VideoLoopAsync(token), token);

                //audio v1: real-time only, see class remarks, gap 2
                if (Math.Abs(Speed - 1f) < 0.0001f)
                {
                    var audioEngine = new PlaybackAudioEngine();
                    _audioEngine = audioEngine;

                    _ = audioEngine
                        .StartAsync(
                            timeline, Blueprint.Framerate, Blueprint.Resolution.Item1, Blueprint.Resolution.Item2,
                            Position, args => OnAudioSample(args), token)
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

        public void Stop()
        {
            CancellationTokenSource? cts;

            lock (_stateLock)
            {
                if (!_isPlaying) return;
                _isPlaying = false;
                cts = _cts;
                _cts = null;
            }

            cts?.Cancel();

            try { _videoTask?.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { /* expected */ }

            _audioEngine?.Dispose();
            _audioEngine = null;

            cts?.Dispose();
            _videoTask = null;
        }

        private async Task VideoLoopAsync(CancellationToken token)
        {
            Timeline timeline = Blueprint.Timeline;
            int width = Blueprint.Resolution.Item1;
            int height = Blueprint.Resolution.Item2;
            int fps = Blueprint.Framerate;

            var tempFiles = new ConcurrentBag<string>();
            var nativeSizes = new ConcurrentDictionary<Clip, (int, int)>();
            var staticImagePaths = new ConcurrentDictionary<Clip, string>();
            var decodePlans = new ConcurrentDictionary<Clip, DecodeHwAccelPlan>();

            try
            {
                await RenderContentPreparation.PrepareContentAsync(
                    timeline, width, height, Blueprint.HardwareAccelerator,
                    nativeSizes, staticImagePaths, decodePlans, tempFiles);

                TimeSpan startPosition = Position;
                Dictionary<Clip, TimeSpan> seekOffsets = ComputeSeekOffsets(timeline, startPosition);

                Dictionary<int, List<Clip>> decoderReleaseSchedule =
                    RenderContentPreparation.BuildDecoderReleaseSchedule(timeline, fps);

                using var contentSource = new SkClipContentSource(
                    fps, nativeSizes, staticImagePaths, decodePlans, seekOffsets);

                using GpuContext gpuContext = GpuContext.Create(Blueprint.HardwareAccelerator);
                using var surfacePool = new SkSurfacePool(
                    gpuContext.GRContext, width, height, timeline.Channels.Count);

                int startFrame = (int)(startPosition.TotalSeconds * fps);
                int totalFrames = Math.Max(1, (int)Math.Ceiling(timeline.Duration.TotalSeconds * fps));

                var clock = Stopwatch.StartNew();

                for (int frameIndex = startFrame; frameIndex < totalFrames; frameIndex++)
                {
                    if (token.IsCancellationRequested) return;

                    FrameState state = FrameStateResolver.Resolve(timeline, frameIndex, fps, nativeSizes);

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
