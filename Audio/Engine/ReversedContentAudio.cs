using System;

namespace EditSharp.Audio.Engine
{
    /// <summary>
    /// A content stream read backwards: stream frame r is content frame -r, so
    /// reading forward walks the content towards its start, for a clip playing in
    /// reverse. The content is read forward a window at a time, stepping back
    /// through it, and handed out reversed. Before the content's start it ends;
    /// past its end it's silence.
    /// </summary>
    internal sealed class ReversedContentAudio(IContentAudio content, int channels) : IContentAudio
    {
        private const int WindowFrames = 16384;

        private readonly IContentAudio _content = content;

        //the next content frame to hand out; frames go out descending from here
        private long _cursor;
        private float[] _window = [];
        private long _windowStart, _windowEnd;
        private int _generation = -1;

        public int Generation => _content.Generation;

        //stream frames are negated content frames, so everything readable is at or below 0
        public long FirstFrame => long.MinValue / 2;

        public bool Ready(bool wait) => _content.Ready(wait);

        public void Seek(long frame)
        {
            _cursor = -frame;
            _windowStart = _windowEnd = 0;
        }

        public int Read(Span<float> destination)
        {
            int frames = destination.Length / channels;
            int done = 0;

            if (_generation != _content.Generation)
            {
                _generation = _content.Generation;
                _windowStart = _windowEnd = 0;
            }

            while (done < frames && _cursor >= 0)
            {
                if (_cursor < _windowStart || _cursor >= _windowEnd) Load(_cursor);

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

        //make the window ending at `frame` (inclusive) current; what the content doesn't have is silence
        private void Load(long frame)
        {
            long end = frame + 1;
            long start = Math.Max(0, end - WindowFrames);
            int length = (int)(end - start);

            if (_window.Length < length * channels) _window = new float[WindowFrames * channels];
            Array.Clear(_window, 0, length * channels);

            _content.Seek(start);
            _content.Read(_window.AsSpan(0, length * channels));

            _windowStart = start;
            _windowEnd = end;
        }

        public void Dispose() => _content.Dispose();
    }
}
