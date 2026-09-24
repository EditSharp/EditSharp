using System;
using EditSharp.Audio.Engine;
using EditSharp.Components.Nodes.Sources;

namespace EditSharp.Audio.Processors
{
    /// <summary>
    /// A ToneGeneratorInputNode's waveform at content rate. Frequency and
    /// amplitude are evaluated every sample, and phase accumulates across
    /// blocks so a changing frequency never jumps. A seek restarts the phase
    /// where a constant tone at the current frequency would be.
    /// </summary>
    internal sealed class ToneContentAudio(ToneGeneratorInputNode node, AudioFormat format) : IContentAudio
    {
        private const double TwoPi = 2.0 * Math.PI;

        private long _position;
        private double _phase;

        public int Generation => 0;

        public bool Ready(bool wait) => true;

        public void Seek(long frame)
        {
            _position = Math.Max(0, frame);
            double t = _position / (double)format.SampleRate;
            _phase = TwoPi * node.Frequency.Evaluate(TimeSpan.FromSeconds(t)) * t % TwoPi;
        }

        public int Read(Span<float> destination)
        {
            int channels = format.Channels;
            int frames = destination.Length / channels;
            Waveform waveform = node.Waveform;

            for (int i = 0; i < frames; i++)
            {
                TimeSpan t = TimeSpan.FromSeconds((_position + i) / (double)format.SampleRate);
                float frequency = node.Frequency.Evaluate(t);
                float amplitude = node.Amplitude.Evaluate(t);

                double value = waveform switch
                {
                    Waveform.Sine => Math.Sin(_phase),
                    Waveform.Square => Math.Sin(_phase) >= 0 ? 1.0 : -1.0,
                    Waveform.Sawtooth => 2.0 * ((_phase / TwoPi) - Math.Floor((_phase / TwoPi) + 0.5)),
                    Waveform.Triangle => (2.0 / Math.PI) * Math.Asin(Math.Sin(_phase)),
                    _ => 0.0,
                };

                float sample = (float)(value * amplitude);
                for (int ch = 0; ch < channels; ch++) destination[i * channels + ch] = sample;

                _phase += TwoPi * frequency / format.SampleRate;
                if (_phase > TwoPi) _phase %= TwoPi;
            }

            _position += frames;
            return frames;
        }

        public void Dispose() { }
    }
}
