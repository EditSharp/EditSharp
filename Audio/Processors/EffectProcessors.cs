using System;
using System.Collections.Generic;
using EditSharp.Audio.Engine;
using EditSharp.Components.Nodes.Effects;

namespace EditSharp.Audio.Processors
{
    /// <summary>Gain, keyframed and optionally multiplied by a Value modulation input.</summary>
    internal sealed class GainProcessor(GainNode node) : IAudioProcessor
    {
        private float[] _curve = [];

        public void Process(in AudioTick tick, AudioPortBuffers ports)
        {
            Span<float> curve = Curve(ref _curve, tick.Frames);
            AudioAutomation.Render(node.Gain, tick, curve);
            AudioAutomation.Modulate(node, "Modulation", tick, curve);

            float[] input = ports.Inputs[0], output = ports.Outputs[0];
            int channels = tick.Format.Channels;

            for (int i = 0; i < tick.Frames; i++)
            {
                float g = curve[i];
                for (int ch = 0; ch < channels; ch++) output[i * channels + ch] = input[i * channels + ch] * g;
            }
        }

        internal static Span<float> Curve(ref float[] buffer, int frames)
        {
            if (buffer.Length < frames) buffer = new float[frames];
            return buffer.AsSpan(0, frames);
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Peaking EQ bands in series (RBJ biquads, Direct Form 1). Each band's
    /// filter history is kept per band object, so adding or removing a band
    /// doesn't disturb the others. Coefficients follow automation every
    /// AudioAutomation.StepFrames frames.
    /// </summary>
    internal sealed class EqProcessor(EQNode node) : IAudioProcessor
    {
        //x1, x2, y1, y2 per channel
        private readonly Dictionary<EQBand, double[]> _history = new(ReferenceEqualityComparer.Instance);
        private float[] _freq = [], _gain = [], _q = [];

        public void Process(in AudioTick tick, AudioPortBuffers ports)
        {
            int channels = tick.Format.Channels;
            int sampleRate = tick.Format.SampleRate;
            float[] output = ports.Outputs[0];
            ports.Inputs[0].AsSpan(0, tick.Samples).CopyTo(output);

            EQBand[] bands = node.Bands.ToArray();
            var live = new HashSet<EQBand>(bands, ReferenceEqualityComparer.Instance);
            foreach (EQBand gone in new List<EQBand>(_history.Keys))
                if (!live.Contains(gone)) _history.Remove(gone);

            foreach (EQBand band in bands)
            {
                if (!_history.TryGetValue(band, out double[]? h)) _history[band] = h = new double[channels * 4];

                Span<float> freq = GainProcessor.Curve(ref _freq, tick.Frames);
                Span<float> gain = GainProcessor.Curve(ref _gain, tick.Frames);
                Span<float> q = GainProcessor.Curve(ref _q, tick.Frames);
                AudioAutomation.Render(band.FrequencyHz, tick, freq);
                AudioAutomation.Render(band.GainDb, tick, gain);
                AudioAutomation.Render(band.Q, tick, q);

                (double b0, double b1, double b2, double a1, double a2) c = default;

                for (int i = 0; i < tick.Frames; i++)
                {
                    if (i % AudioAutomation.StepFrames == 0)
                        c = Peaking(Math.Clamp(freq[i], 10.0, sampleRate / 2.0 - 10.0), gain[i], Math.Max(0.05, q[i]), sampleRate);

                    for (int ch = 0; ch < channels; ch++)
                    {
                        int s = ch * 4;
                        double x0 = output[i * channels + ch];
                        double y0 = c.b0 * x0 + c.b1 * h[s] + c.b2 * h[s + 1] - c.a1 * h[s + 2] - c.a2 * h[s + 3];

                        h[s + 1] = h[s]; h[s] = x0;
                        h[s + 3] = h[s + 2]; h[s + 2] = y0;

                        output[i * channels + ch] = (float)y0;
                    }
                }
            }
        }

        internal static (double b0, double b1, double b2, double a1, double a2) Peaking(double freq, double gainDb, double q, int sampleRate)
        {
            double a = Math.Pow(10, gainDb / 40.0);
            double w0 = 2 * Math.PI * freq / sampleRate;
            double alpha = Math.Sin(w0) / (2 * q);
            double cos = Math.Cos(w0);
            double a0 = 1 + alpha / a;

            return ((1 + alpha * a) / a0, -2 * cos / a0, (1 - alpha * a) / a0, -2 * cos / a0, (1 - alpha / a) / a0);
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Peak-sensing feed-forward compressor. The envelope carries across
    /// blocks; attack and release smooth it in the dB domain.
    /// </summary>
    internal sealed class CompressorProcessor(CompressorNode node) : IAudioProcessor
    {
        private const double FloorDb = -120.0;

        private double _envelopeDb = FloorDb;
        private float[] _threshold = [], _ratio = [], _attack = [], _release = [], _makeup = [];

        public void Process(in AudioTick tick, AudioPortBuffers ports)
        {
            int frames = tick.Frames, channels = tick.Format.Channels, sampleRate = tick.Format.SampleRate;

            Span<float> threshold = GainProcessor.Curve(ref _threshold, frames);
            Span<float> ratio = GainProcessor.Curve(ref _ratio, frames);
            Span<float> attack = GainProcessor.Curve(ref _attack, frames);
            Span<float> release = GainProcessor.Curve(ref _release, frames);
            Span<float> makeup = GainProcessor.Curve(ref _makeup, frames);
            AudioAutomation.Render(node.Threshold, tick, threshold);
            AudioAutomation.Render(node.Ratio, tick, ratio);
            AudioAutomation.Render(node.AttackMs, tick, attack);
            AudioAutomation.Render(node.ReleaseMs, tick, release);
            AudioAutomation.Render(node.MakeupGainDb, tick, makeup);

            float[] input = ports.Inputs[0], output = ports.Outputs[0];

            for (int i = 0; i < frames; i++)
            {
                double peak = 0.0;
                for (int ch = 0; ch < channels; ch++) peak = Math.Max(peak, Math.Abs((double)input[i * channels + ch]));

                double inputDb = peak <= 1e-9 ? FloorDb : 20.0 * Math.Log10(peak);
                double ms = inputDb > _envelopeDb ? Math.Max(0.01, attack[i]) : Math.Max(0.01, release[i]);
                double coeff = Math.Exp(-1.0 / (sampleRate * (ms / 1000.0)));
                _envelopeDb = coeff * _envelopeDb + (1 - coeff) * inputDb;

                double reductionDb = _envelopeDb > threshold[i] ? (threshold[i] - _envelopeDb) * (1.0 - 1.0 / Math.Max(1.0, ratio[i])) : 0.0;
                double gain = Math.Pow(10, (reductionDb + makeup[i]) / 20.0);

                for (int ch = 0; ch < channels; ch++) output[i * channels + ch] = (float)(input[i * channels + ch] * gain);
            }
        }

        public void Dispose() { }
    }

    /// <summary>Weighted sum of two inputs; each weight keyframed and optionally modulated.</summary>
    internal sealed class AudioMixProcessor(AudioMixNode node) : IAudioProcessor
    {
        private float[] _a = [], _b = [];

        public void Process(in AudioTick tick, AudioPortBuffers ports)
        {
            Span<float> wa = GainProcessor.Curve(ref _a, tick.Frames);
            Span<float> wb = GainProcessor.Curve(ref _b, tick.Frames);
            AudioAutomation.Render(node.MixA, tick, wa);
            AudioAutomation.Render(node.MixB, tick, wb);
            AudioAutomation.Modulate(node, "MixAModulation", tick, wa);
            AudioAutomation.Modulate(node, "MixBModulation", tick, wb);

            float[] a = ports.Inputs[0], b = ports.Inputs[1], output = ports.Outputs[0];
            int channels = tick.Format.Channels;

            for (int i = 0; i < tick.Frames; i++)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    int s = i * channels + ch;
                    output[s] = a[s] * wa[i] + b[s] * wb[i];
                }
            }
        }

        public void Dispose() { }
    }
}
