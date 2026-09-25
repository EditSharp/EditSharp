using EditSharp.Components.Media;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EditSharp.Editing;
using EditSharp.History;

namespace EditSharp.Components.Nodes.Input
{
    /// <summary>The shape of a tone's wave.</summary>
    public enum Waveform
    {
        /// <summary>A pure tone.</summary>
        Sine,

        /// <summary>Alternates between full positive and full negative.</summary>
        Square,

        /// <summary>Ramps up, then drops.</summary>
        Sawtooth,

        /// <summary>Ramps up, then down.</summary>
        Triangle,
    }

    /// <summary>A synthesized tone.</summary>
    /// <remarks>Frequency and amplitude are evaluated every sample, and the phase carries on across blocks, so a changing frequency never clicks.</remarks>
    [NodeKind("tone", DisplayName = "Tone")]
    public sealed class ToneNode : AudioInputNode
    {
        Waveform _waveform = Waveform.Sine;
        /// <summary>The shape of the wave.</summary>
        [Editable("Waveform")]
        public Waveform Waveform { get => _waveform; set => Transaction.Set(this, ref _waveform, value, static (o, v) => o._waveform = v); }

        Animatable<float> _frequency = new(440f);
        /// <summary>The pitch, in hertz; 440 by default.</summary>
        [Editable("Frequency", Min = 20, Max = 20000, Step = 1, Unit = "Hz")]
        public Animatable<float> Frequency { get => _frequency; set => Transaction.Set(this, ref _frequency, value, static (o, v) => o._frequency = v); }

        Animatable<float> _amplitude = new(1f);
        /// <summary>The volume, from 0 (silent) to 1 (full scale).</summary>
        [Editable("Amplitude", Min = 0, Max = 1, Step = 0.01)]
        public Animatable<float> Amplitude { get => _amplitude; set => Transaction.Set(this, ref _amplitude, value, static (o, v) => o._amplitude = v); }

        /// <inheritdoc/>
        public override IEnumerable<IAnimatable> Animatables => [Frequency, Amplitude];

        /// <summary>Always null: a tone has no end of its own.</summary>
        /// <param name="ct">Unused.</param>
        /// <returns>Null.</returns>
        public override Task<TimeSpan?> GetNaturalLengthAsync(CancellationToken ct = default) => Task.FromResult<TimeSpan?>(null);

        internal override Task<IPreparedAudioSource> PrepareAsync(CancellationToken ct = default) =>
            Task.FromResult<IPreparedAudioSource>(new Prepared(this));

        private sealed class Prepared(ToneNode node) : IPreparedAudioSource
        {
            public IAudioSampleReader OpenReader(AudioReaderOptions options) => new Reader(node, options);

            public void Dispose() { }
        }

        /// <summary>Content frames from StartAt on; Duration ends it (or loops it, keeping phase), as for any input.</summary>
        private sealed class Reader : IAudioSampleReader
        {
            private const double TwoPi = 2.0 * System.Math.PI;

            private readonly ToneNode _node;
            private readonly int _rate, _channels;
            private long _position;
            private double _phase;
            private bool _ended;

            public Reader(ToneNode node, AudioReaderOptions options)
            {
                _node = node;
                _rate = options.SampleRate;
                _channels = options.Channels;
                _position = (long)System.Math.Round(options.StartAt.TotalSeconds * _rate);

                //where a steady tone at the starting frequency would be
                double t = _position / (double)_rate;
                _phase = TwoPi * node.Frequency.Evaluate(options.StartAt) * t % TwoPi;
            }

            public int Read(Span<float> destination)
            {
                if (_ended) throw new SourceUnavailableException(SourceUnavailableReason.EndOfSource, "The tone has ended.");

                long? window = _node.ResolveWindow(null).Length is { } d ? (long)System.Math.Floor(d.TotalSeconds * _rate) : null;
                int frames = destination.Length / _channels;
                Waveform waveform = _node.Waveform;

                for (int i = 0; i < frames; i++)
                {
                    if (window is { } w && _position >= w)
                    {
                        if (!_node.Loop || w <= 0) { _ended = true; return i; }
                        _position %= w;
                    }

                    TimeSpan t = TimeSpan.FromSeconds(_position / (double)_rate);
                    double value = waveform switch
                    {
                        Waveform.Sine => System.Math.Sin(_phase),
                        Waveform.Square => System.Math.Sin(_phase) >= 0 ? 1.0 : -1.0,
                        Waveform.Sawtooth => 2.0 * ((_phase / TwoPi) - System.Math.Floor((_phase / TwoPi) + 0.5)),
                        Waveform.Triangle => (2.0 / System.Math.PI) * System.Math.Asin(System.Math.Sin(_phase)),
                        _ => 0.0,
                    };

                    float sample = (float)(value * _node.Amplitude.Evaluate(t));
                    for (int ch = 0; ch < _channels; ch++) destination[i * _channels + ch] = sample;

                    _phase += TwoPi * _node.Frequency.Evaluate(t) / _rate;
                    if (_phase > TwoPi) _phase %= TwoPi;
                    _position++;
                }

                return frames;
            }

            public void Dispose() { }
        }
    }
}
