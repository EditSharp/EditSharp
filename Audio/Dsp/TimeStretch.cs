using System;
using System.Collections.Generic;
using EditSharp.Audio.Engine;

namespace EditSharp.Audio.Dsp
{
    /// <summary>
    /// Changes speed without changing pitch, as a stream: Reset puts content
    /// frame `start` at output frame 0, then each Render continues the output
    /// while consuming content at `speed` content frames per output frame.
    /// </summary>
    internal interface ITimeStretch
    {
        void Reset(double start, double speed);

        void Render(ContentWindow window, double speed, int frames, Span<float> output);
    }

    /// <summary>Output frames produced ahead of what's been asked for, interleaved.</summary>
    internal sealed class OutputQueue(int channels)
    {
        private float[] _samples = new float[4096 * channels];
        private int _start, _count;

        public int Frames => _count / channels;

        public void Add(ReadOnlySpan<float> samples)
        {
            if (_start + _count + samples.Length > _samples.Length)
            {
                if (_count + samples.Length > _samples.Length) Array.Resize(ref _samples, Math.Max(_samples.Length * 2, _count + samples.Length));
                Array.Copy(_samples, _start, _samples, 0, _count);
                _start = 0;
            }

            samples.CopyTo(_samples.AsSpan(_start + _count));
            _count += samples.Length;
        }

        public void Take(Span<float> destination)
        {
            _samples.AsSpan(_start, destination.Length).CopyTo(destination);
            Skip(destination.Length / channels);
        }

        public void Skip(int frames)
        {
            int samples = Math.Min(_count, frames * channels);
            _start += samples;
            _count -= samples;
            if (_count == 0) _start = 0;
        }

        public void Clear() => _start = _count = 0;
    }

    /// <summary>
    /// WSOLA (waveform-similarity overlap-add). Grains of 40 ms are taken from
    /// the content every Hs * speed frames and overlap-added every Hs = 20 ms
    /// of output with Hann windows. Each grain's start is nudged within
    /// ±10 ms to where the content best matches the natural continuation of
    /// the previous grain, which keeps waveforms aligned across the joins.
    /// The search runs on a mono mix, coarse then fine.
    /// </summary>
    internal sealed class Wsola : ITimeStretch
    {
        private readonly int _channels;
        private readonly int _size;
        private readonly int _hop;
        private readonly int _search;
        private readonly float[] _window;
        private readonly float[] _overlap;
        private readonly OutputQueue _queue;

        private double _analysis;
        private long _previous = long.MinValue;
        private int _skip;
        private bool _preroll;

        public Wsola(AudioFormat format)
        {
            _channels = format.Channels;
            _size = (int)Math.Round(format.SampleRate * 0.040) & ~1;
            _hop = _size / 2;
            _search = (int)Math.Round(format.SampleRate * 0.010);
            _window = Hann(_size);
            _overlap = new float[_size * _channels];
            _queue = new OutputQueue(_channels);
        }

        public void Reset(double start, double speed)
        {
            //one grain of pre-roll a hop before `start`, so output frame 0 already has two grains
            //overlapping it; it's spaced at 1x so both cover the content at `start`
            _analysis = start - _hop;
            _previous = long.MinValue;
            Array.Clear(_overlap);
            _queue.Clear();
            _skip = _hop;
            _preroll = true;
        }

        public void Render(ContentWindow window, double speed, int frames, Span<float> output)
        {
            while (_queue.Frames < frames + _skip) Grain(window, speed);

            _queue.Skip(_skip);
            _skip = 0;
            _queue.Take(output[..(frames * _channels)]);
        }

        private void Grain(ContentWindow window, double speed)
        {
            long nominal = (long)Math.Round(_analysis);
            long start = nominal;

            if (_previous != long.MinValue)
            {
                long natural = _previous + _hop;
                window.Ensure(Math.Min(natural, nominal - _search) - _size, Math.Max(natural, nominal + _search) + _size);
                start = BestMatch(window, natural, nominal);
            }
            else
            {
                window.Ensure(start - _size, start + _size);
            }

            for (int i = 0; i < _size; i++)
                for (int ch = 0; ch < _channels; ch++)
                    _overlap[i * _channels + ch] += window.At(start + i, ch) * _window[i];

            //the first half is now complete
            _queue.Add(_overlap.AsSpan(0, _hop * _channels));
            Array.Copy(_overlap, _hop * _channels, _overlap, 0, (_size - _hop) * _channels);
            Array.Clear(_overlap, (_size - _hop) * _channels, _hop * _channels);

            _previous = start;
            _analysis += _preroll ? _hop : speed * _hop;
            _preroll = false;
        }

        //the start near `nominal` whose first half best matches the content that naturally follows the previous grain
        private long BestMatch(ContentWindow window, long natural, long nominal)
        {
            const int Coarse = 4;
            int length = _hop;

            long best = nominal;
            double bestScore = double.NegativeInfinity;

            for (int pass = 0; pass < 2; pass++)
            {
                long from = pass == 0 ? nominal - _search : best - Coarse;
                long to = pass == 0 ? nominal + _search : best + Coarse;
                int step = pass == 0 ? Coarse : 1;

                for (long candidate = from; candidate <= to; candidate += step)
                {
                    double dot = 0, energy = 0;
                    for (int i = 0; i < length; i += 2)
                    {
                        double a = Mono(window, natural + i), b = Mono(window, candidate + i);
                        dot += a * b;
                        energy += b * b;
                    }

                    double score = energy > 1e-12 ? dot / Math.Sqrt(energy) : 0;
                    if (score > bestScore) { bestScore = score; best = candidate; }
                }
            }

            return best;
        }

        private double Mono(ContentWindow window, long frame)
        {
            double sum = 0;
            for (int ch = 0; ch < _channels; ch++) sum += window.At(frame, ch);
            return sum;
        }

        internal static float[] Hann(int size)
        {
            var w = new float[size];
            for (int i = 0; i < size; i++) w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size));
            return w;
        }
    }

    /// <summary>
    /// Phase vocoder with identity phase locking (Laroche and Dolson). Frames
    /// of 2048 are analysed every Hs * speed content frames and resynthesised
    /// every Hs = 512 output frames. Each spectral peak's phase advances at
    /// its measured frequency; the bins around a peak keep their phase
    /// relative to it, which removes most of the "phasey" sound of a plain
    /// vocoder. Channels are processed independently.
    /// </summary>
    internal sealed class PhaseVocoder : ITimeStretch
    {
        private const int Size = 2048;
        private const int Hop = Size / 4;
        private const int Bins = Size / 2 + 1;

        //sum of squared Hann windows at 4x overlap
        private const double OverlapGain = 1.5;

        private readonly int _channels;
        private readonly Fft _fft = new(Size);
        private readonly float[] _window = Wsola.Hann(Size);
        private readonly double[] _re = new double[Size], _im = new double[Size];
        private readonly double[] _magnitude = new double[Bins], _phase = new double[Bins];
        private readonly double[][] _lastPhase, _synthPhase;
        private readonly float[] _overlap;
        private readonly int[] _peakOf = new int[Bins];
        private readonly OutputQueue _queue;

        private double _analysis;
        private long _previous = long.MinValue;
        private int _skip;
        private int _preroll;

        public PhaseVocoder(AudioFormat format)
        {
            _channels = format.Channels;
            _lastPhase = new double[_channels][];
            _synthPhase = new double[_channels][];
            for (int ch = 0; ch < _channels; ch++) { _lastPhase[ch] = new double[Bins]; _synthPhase[ch] = new double[Bins]; }
            _overlap = new float[Size * _channels];
            _queue = new OutputQueue(_channels);
        }

        public void Reset(double start, double speed)
        {
            //three frames of pre-roll, so output frame 0 has all four overlapping frames; spaced
            //at 1x so they all cover the content at `start`
            _analysis = start - 3 * Hop;
            _previous = long.MinValue;
            Array.Clear(_overlap);
            _queue.Clear();
            _skip = 3 * Hop;
            _preroll = 3;
        }

        public void Render(ContentWindow window, double speed, int frames, Span<float> output)
        {
            while (_queue.Frames < frames + _skip) Frame(window, speed);

            _queue.Skip(_skip);
            _skip = 0;
            _queue.Take(output[..(frames * _channels)]);
        }

        private void Frame(ContentWindow window, double speed)
        {
            long start = (long)Math.Round(_analysis);
            long advance = _previous == long.MinValue ? 0 : start - _previous;
            window.Ensure(start - Size, start + Size);

            for (int ch = 0; ch < _channels; ch++)
            {
                for (int i = 0; i < Size; i++)
                {
                    _re[i] = window.At(start + i, ch) * _window[i];
                    _im[i] = 0;
                }

                _fft.Transform(_re, _im);

                for (int k = 0; k < Bins; k++)
                {
                    _magnitude[k] = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
                    _phase[k] = Math.Atan2(_im[k], _re[k]);
                }

                double[] last = _lastPhase[ch], synth = _synthPhase[ch];

                if (advance <= 0)
                {
                    Array.Copy(_phase, synth, Bins);
                }
                else
                {
                    FindPeaks();

                    //peaks advance at their measured frequency
                    for (int k = 0; k < Bins; k++)
                    {
                        if (_peakOf[k] != k) continue;

                        double expected = 2 * Math.PI * k * advance / Size;
                        double deviation = Wrap(_phase[k] - last[k] - expected);
                        double frequency = (expected + deviation) / advance;
                        synth[k] += frequency * Hop;
                    }

                    //everything else keeps its phase relative to its peak
                    for (int k = 0; k < Bins; k++)
                    {
                        int peak = _peakOf[k];
                        if (peak != k) synth[k] = synth[peak] + (_phase[k] - _phase[peak]);
                    }
                }

                Array.Copy(_phase, last, Bins);

                for (int k = 0; k < Bins; k++)
                {
                    _re[k] = _magnitude[k] * Math.Cos(synth[k]);
                    _im[k] = _magnitude[k] * Math.Sin(synth[k]);
                }

                //a real signal's spectrum mirrors
                for (int k = Bins; k < Size; k++)
                {
                    _re[k] = _re[Size - k];
                    _im[k] = -_im[Size - k];
                }

                _fft.Transform(_re, _im, inverse: true);

                for (int i = 0; i < Size; i++)
                    _overlap[i * _channels + ch] += (float)(_re[i] * _window[i] / OverlapGain);
            }

            _queue.Add(_overlap.AsSpan(0, Hop * _channels));
            Array.Copy(_overlap, Hop * _channels, _overlap, 0, (Size - Hop) * _channels);
            Array.Clear(_overlap, (Size - Hop) * _channels, Hop * _channels);

            _previous = start;
            _analysis += _preroll > 0 ? Hop : speed * Hop;
            if (_preroll > 0) _preroll--;
        }

        //each bin's owning peak: the nearest local maximum of the magnitude spectrum
        private void FindPeaks()
        {
            int lastPeak = -1;
            for (int k = 0; k < Bins; k++)
            {
                bool peak = (k < 2 || _magnitude[k] > _magnitude[k - 1] && _magnitude[k] > _magnitude[k - 2])
                         && (k > Bins - 3 || _magnitude[k] >= _magnitude[k + 1] && _magnitude[k] >= _magnitude[k + 2]);

                if (!peak) continue;

                //bins between two peaks split at the midpoint
                int from = lastPeak < 0 ? 0 : (lastPeak + k) / 2 + 1;
                for (int b = from; b <= k; b++) _peakOf[b] = k;
                if (lastPeak >= 0) for (int b = lastPeak + 1; b < from; b++) _peakOf[b] = lastPeak;
                lastPeak = k;
            }

            if (lastPeak < 0) { for (int k = 0; k < Bins; k++) _peakOf[k] = k; return; }
            for (int b = lastPeak + 1; b < Bins; b++) _peakOf[b] = lastPeak;
        }

        private static double Wrap(double phase) => phase - 2 * Math.PI * Math.Round(phase / (2 * Math.PI));
    }
}
