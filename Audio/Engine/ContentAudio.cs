using System;
using System.Collections.Generic;

namespace EditSharp.Audio.Engine
{
    /// <summary>
    /// A clip input's own audio at content rate (1x, in-point at frame 0),
    /// before any speed change: a media source, a tone, a nested timeline.
    /// </summary>
    internal interface IContentAudio : IDisposable
    {
        /// <summary>
        /// Whether reads can start. Starts any preparation; with `wait` it
        /// blocks until preparation is done (exports), otherwise the caller
        /// plays silence for now (previews).
        /// </summary>
        bool Ready(bool wait);

        /// <summary>Changes whenever the stream becomes readable afresh (a source prepared again after failing); readers of it start over.</summary>
        int Generation { get; }

        /// <summary>Moves the next Read to content frame `frame`.</summary>
        void Seek(long frame);

        /// <summary>The next frames, interleaved. Fewer than asked means the material ended (or failed) there.</summary>
        int Read(Span<float> destination);
    }

    /// <summary>
    /// Turns a content-rate stream into timeline-rate blocks at the clip's
    /// speed. Keeps a window of content frames by absolute index, so blocks
    /// that follow on from each other read on without seeking, while a jump
    /// (a seek, a trim, a speed edit) re-seeks the stream.
    ///
    /// Varispeed here is linear interpolation (pitch follows speed).
    /// </summary>
    internal sealed class ContentWarp(IContentAudio content, int channels) : IDisposable
    {
        private const int SeekSlack = 4096;

        private float[] _window = new float[4096 * channels];
        private long _windowStart;
        private int _windowFrames;
        private long? _endedAt;
        private int _generation = content.Generation;

        public IContentAudio Content { get; } = content;

        /// <summary>
        /// Fills `output` (frames * channels) with content sampled at
        /// start, start + step, start + 2 step, ... in content frames. Past the
        /// material's end it's silence.
        /// </summary>
        public void Render(double start, double step, int frames, Span<float> output)
        {
            double last = start + step * (frames - 1);
            long first = (long)Math.Floor(Math.Min(start, last));
            long needEnd = (long)Math.Floor(Math.Max(start, last)) + 2;

            Fill(first, needEnd);

            for (int i = 0; i < frames; i++)
            {
                double position = start + step * i;
                long index = (long)Math.Floor(position);
                float fraction = (float)(position - index);

                for (int ch = 0; ch < channels; ch++)
                {
                    float a = SampleAt(index, ch);
                    float b = fraction == 0f ? 0f : SampleAt(index + 1, ch);
                    output[i * channels + ch] = a + (b - a) * fraction;
                }
            }
        }

        private float SampleAt(long frame, int channel)
        {
            long offset = frame - _windowStart;
            return offset >= 0 && offset < _windowFrames ? _window[offset * channels + channel] : 0f;
        }

        //make the window hold [first, end) as far as the material reaches
        private void Fill(long first, long end)
        {
            if (first < 0) first = 0;

            bool continues = first >= _windowStart && first <= _windowStart + _windowFrames + SeekSlack && _generation == Content.Generation;
            _generation = Content.Generation;
            if (!continues)
            {
                Content.Seek(first);
                _windowStart = first;
                _windowFrames = 0;
                _endedAt = null;
            }

            //drop what's behind
            int drop = (int)Math.Min(_windowFrames, first - _windowStart);
            if (drop > 0)
            {
                Array.Copy(_window, drop * channels, _window, 0, (_windowFrames - drop) * channels);
                _windowStart += drop;
                _windowFrames -= drop;
            }

            long have = _windowStart + _windowFrames;
            if (have >= end || _endedAt is { } ended && have >= ended) return;

            int want = (int)(end - have);
            if ((_windowFrames + want) * channels > _window.Length)
                Array.Resize(ref _window, Math.Max(_window.Length * 2, (_windowFrames + want) * channels));

            int got = Content.Read(_window.AsSpan(_windowFrames * channels, want * channels));
            _windowFrames += got;
            if (got < want) _endedAt = _windowStart + _windowFrames;
        }

        public void Dispose() => Content.Dispose();
    }
}
