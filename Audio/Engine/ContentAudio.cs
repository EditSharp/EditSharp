using System;
using EditSharp.Audio.Dsp;
using EditSharp.Components.Clips;

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
    /// speed. At exactly 1x on whole frames it copies. Otherwise
    /// PitchPreservation picks the stage: None resamples (windowed sinc, pitch
    /// follows speed); WSOLA and PhaseVocoder stretch time and keep pitch.
    /// The stretchers carry state from block to block and start over only on
    /// a real discontinuity: a block that doesn't follow on from the last one
    /// (a seek, a trim, a speed jump), a seek of the content, or a mode change.
    /// </summary>
    internal sealed class ContentWarp(IContentAudio content, AudioFormat format) : IDisposable
    {
        private readonly ContentWindow _window = new(content, format.Channels);
        private ITimeStretch? _stretch;
        private PitchPreservation _mode;
        private double _expected = double.NaN;
        private int _epoch = -1;

        public IContentAudio Content => _window.Content;

        /// <summary>
        /// Fills `output` with `frames` frames of content taken from content
        /// frame `start` onwards at `step` content frames per output frame.
        /// </summary>
        public void Render(double start, double step, int frames, PitchPreservation pitch, Span<float> output)
        {
            bool continues = Math.Abs(start - _expected) <= 1 + Math.Abs(step) && _epoch == _window.Epoch;
            _expected = start + step * frames;
            Span<float> target = output[..(frames * format.Channels)];

            if (Math.Abs(step - 1) < 1e-9 && Math.Abs(start - Math.Round(start)) < 1e-6)
            {
                long first = (long)Math.Round(start);
                _window.Ensure(first, first + frames);
                _window.CopyTo(first, frames, target);
                _stretch = null;
            }
            else if (pitch == PitchPreservation.None)
            {
                SincResampler.Render(_window, start, step, frames, target);
                _stretch = null;
            }
            else
            {
                if (_stretch is null || _mode != pitch || !continues)
                {
                    _stretch = pitch == PitchPreservation.PhaseVocoder ? new PhaseVocoder(format) : new Wsola(format);
                    _stretch.Reset(start, step);
                    _mode = pitch;
                }

                _stretch.Render(_window, step, frames, target);
            }

            _epoch = _window.Epoch;
        }

        public void Dispose() => Content.Dispose();
    }
}
