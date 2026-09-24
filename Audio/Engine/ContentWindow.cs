using System;

namespace EditSharp.Audio.Engine
{
    /// <summary>
    /// A sliding window of a content stream's frames, addressed by absolute
    /// content frame. Asking for a range that continues from what's held reads
    /// on; anything else seeks the stream (and bumps Epoch, so stateful
    /// readers of the window know to start over). Frames before 0 or past the
    /// material's end read as silence.
    /// </summary>
    internal sealed class ContentWindow(IContentAudio content, int channels)
    {
        private const int SeekSlack = 4096;

        //kept behind each request, so readers that reach back a little (a WSOLA search, a sinc kernel) don't force a seek
        private const int KeepBehind = 16384;

        private float[] _frames = new float[8192 * channels];
        private long _start;
        private int _count;
        private long? _endedAt;
        private int _generation = content.Generation;

        public IContentAudio Content { get; } = content;

        public int Channels { get; } = channels;

        /// <summary>Changes on every seek.</summary>
        public int Epoch { get; private set; }

        /// <summary>Makes [first, end) available, as far as the material reaches.</summary>
        public void Ensure(long first, long end)
        {
            if (first < 0) first = 0;
            if (end <= first) return;

            bool continues = first >= _start && first <= _start + _count + SeekSlack && _generation == Content.Generation;
            _generation = Content.Generation;

            if (!continues)
            {
                Content.Seek(first);
                _start = first;
                _count = 0;
                _endedAt = null;
                Epoch++;
            }

            //drop what's well behind
            int drop = (int)Math.Clamp(first - KeepBehind - _start, 0, _count);
            if (drop > 0)
            {
                Array.Copy(_frames, drop * Channels, _frames, 0, (_count - drop) * Channels);
                _start += drop;
                _count -= drop;
            }

            long have = _start + _count;
            if (have >= end || _endedAt is { } ended && have >= ended) return;

            int want = (int)(end - have);
            if ((_count + want) * Channels > _frames.Length)
                Array.Resize(ref _frames, Math.Max(_frames.Length * 2, (_count + want) * Channels));

            int got = Content.Read(_frames.AsSpan(_count * Channels, want * Channels));
            _count += got;
            if (got < want) _endedAt = _start + _count;
        }

        public float At(long frame, int channel)
        {
            long offset = frame - _start;
            return offset >= 0 && offset < _count ? _frames[offset * Channels + channel] : 0f;
        }

        /// <summary>Copies `frames` frames from `first` into `destination` (interleaved), silence where there's no material.</summary>
        public void CopyTo(long first, int frames, Span<float> destination)
        {
            for (int i = 0; i < frames; i++)
                for (int ch = 0; ch < Channels; ch++)
                    destination[i * Channels + ch] = At(first + i, ch);
        }
    }
}
