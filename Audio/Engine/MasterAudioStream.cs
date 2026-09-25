using System;
using System.Threading.Tasks;
using EditSharp.Components;
using EditSharp.Components.Clips;

namespace EditSharp.Audio.Engine
{
    /// <summary>
    /// The master output at a playback speed: the timeline mix rendered at 1x,
    /// then taken through one ContentWarp at |speed| with the chosen pitch mode
    /// (the same stages clips use). A negative speed plays the forward mix
    /// backwards (see ReversedTimelineAudio). Position is the timeline frame
    /// the next output frame comes from.
    /// </summary>
    internal sealed class MasterAudioStream : IDisposable
    {
        private readonly AudioSession _session;
        private readonly double _speed;
        private readonly PitchPreservation _pitch;
        private readonly ContentWarp _warp;
        private readonly TimelineAudioRenderer _renderer;
        private readonly long _start;

        //in the warp's own coordinates: timeline frames forward, negated timeline frames in reverse
        private double _cursor;

        public MasterAudioStream(Timeline timeline, AudioSession session, long start, double speed, PitchPreservation pitch)
        {
            if (speed == 0) throw new ArgumentOutOfRangeException(nameof(speed), "Speed can't be 0.");

            _session = session;
            _speed = speed;
            _pitch = pitch;

            _renderer = new TimelineAudioRenderer(timeline, session);
            _start = start;

            IContentAudio mix = speed > 0
                ? new TimelineContentAudio(_renderer, session)
                : new ReversedTimelineAudio(_renderer, session);

            _warp = new ContentWarp(mix, session.Format);
            _cursor = speed > 0 ? start : -start;
        }

        public double Position => _speed > 0 ? _cursor : -_cursor;

        /// <summary>Prepares the sources audible where the stream starts, waiting for them.</summary>
        public void PrepareStart() => _renderer.PrepareAt(_speed > 0 ? _start : _start - 1);

        /// <summary>Fills `output` (whole frames) with the next stretch of master audio.</summary>
        public void Read(Span<float> output)
        {
            int frames = output.Length / _session.Format.Channels;
            double step = Math.Abs(_speed);

            _warp.Render(_cursor, step, frames, _pitch, output);
            _cursor += step * frames;
        }

        public void Dispose() => _warp.Dispose();
    }

    /// <summary>A timeline's forward mix as a content stream, rendered a block at a time.</summary>
    internal sealed class TimelineContentAudio(TimelineAudioRenderer renderer, AudioSession session) : IContentAudio
    {
        private long _position;

        public int Generation => 0;

        public bool Ready(bool wait) => true;

        public void Seek(long frame) => _position = frame;

        public int Read(Span<float> destination)
        {
            int channels = session.Format.Channels;
            int frames = destination.Length / channels;

            for (int done = 0; done < frames;)
            {
                int block = Math.Min(session.BlockFrames, frames - done);
                renderer.Render(_position + done, destination.Slice(done * channels, block * channels));
                done += block;
            }

            _position += frames;
            return frames;
        }

        public void Dispose() => renderer.Dispose();
    }

    /// <summary>
    /// A timeline's forward mix, read backwards: stream frame r is timeline
    /// frame -r, so reading forward walks the timeline towards its start. The
    /// mix is rendered forward in one-second windows that step back through
    /// the timeline, each after a short warm-up so filters and compressors are
    /// already settled where it starts, then handed out reversed. The window
    /// before the current one renders in the background while this one plays.
    /// Before the timeline's start it ends.
    /// </summary>
    internal sealed class ReversedTimelineAudio : IContentAudio
    {
        private static readonly TimeSpan WindowLength = TimeSpan.FromSeconds(1);
        //long enough for typical compressor attack/release to settle; slower settings differ slightly just after each window's start
        private static readonly TimeSpan WarmUp = TimeSpan.FromMilliseconds(500);

        private readonly TimelineAudioRenderer _renderer;
        private readonly AudioSession _session;
        private readonly int _windowFrames;
        private readonly int _warmUpFrames;

        //the next timeline frame to hand out; frames go out descending from here
        private long _cursor;

        private float[] _window = [];
        private long _windowStart, _windowEnd;
        private Task<(float[] Samples, long Start, long End)>? _next;

        public ReversedTimelineAudio(TimelineAudioRenderer renderer, AudioSession session)
        {
            _renderer = renderer;
            _session = session;
            _windowFrames = (int)session.FrameOf(WindowLength);
            _warmUpFrames = (int)session.FrameOf(WarmUp);
        }

        public int Generation => 0;

        //stream frames are negated timeline frames, so everything readable is at or below 0
        public long FirstFrame => long.MinValue / 2;

        public bool Ready(bool wait) => true;

        public void Seek(long frame)
        {
            _cursor = -frame;
            _window = [];
            _windowStart = _windowEnd = 0;
            Settle();
        }

        public int Read(Span<float> destination)
        {
            int channels = _session.Format.Channels;
            int frames = destination.Length / channels;
            int done = 0;

            while (done < frames && _cursor >= 0)
            {
                if (_cursor < _windowStart || _cursor >= _windowEnd) Load(_cursor);

                //hand out this window's frames, newest first
                while (done < frames && _cursor >= _windowStart)
                {
                    long offset = (_cursor - _windowStart) * channels;
                    for (int ch = 0; ch < channels; ch++) destination[done * channels + ch] = _window[offset + ch];
                    done++;
                    _cursor--;
                }
            }

            return done;
        }

        //make the window ending at `frame` (inclusive) current, and start rendering the one before it
        private void Load(long frame)
        {
            long end = frame + 1;
            long start = Math.Max(0, end - _windowFrames);

            //the renderer is used by one thread at a time: the prefetch always finishes first
            (float[] Samples, long Start, long End)? prefetched = Settle();
            (float[] samples, long s, long e) = prefetched is { } p && p.Start <= start && p.End >= end
                ? p
                : RenderWindow(start, end);

            _window = samples;
            _windowStart = s;
            _windowEnd = e;

            if (s > 0)
            {
                long previousStart = Math.Max(0, s - _windowFrames);
                _next = Task.Run(() => RenderWindow(previousStart, s));
            }
        }

        //waits for any background render and returns what it made
        private (float[] Samples, long Start, long End)? Settle()
        {
            if (_next is not { } pending) return null;
            _next = null;
            try { return pending.GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogWarning($"Rendering reversed audio ahead failed: {ex.Message}");
                return null;
            }
        }

        //the forward mix of [start, end), rendered from a little earlier so its DSP has settled
        private (float[] Samples, long Start, long End) RenderWindow(long start, long end)
        {
            int channels = _session.Format.Channels;
            long from = Math.Max(0, start - _warmUpFrames);
            var samples = new float[(end - from) * channels];

            for (long frame = from; frame < end;)
            {
                int block = (int)Math.Min(_session.BlockFrames, end - frame);
                _renderer.Render(frame, samples.AsSpan((int)(frame - from) * channels, block * channels));
                frame += block;
            }

            var window = new float[(end - start) * channels];
            Array.Copy(samples, (start - from) * channels, window, 0, window.Length);
            return (window, start, end);
        }

        public void Dispose()
        {
            Settle();
            _renderer.Dispose();
        }
    }
}
