using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components.Effects;
using EditSharp.Components;
 
namespace EditSharp.Composite
{
    /// <summary>
    /// A REAL topological-order graph walker for the AUDIO domain
    /// EffectGraph — the audio-side counterpart to EffectGraphEvaluatorSk,
    /// sharing its exact walking algorithm (EffectGraphTopology.Order) and
    /// its own-port-cache-per-node-per-visit shape, but dispatching genuine
    /// per-node SIGNAL PROCESSING on AudioBuffer instead of per-node image
    /// compositing on SKImage.
    ///
    /// "CLIPS ARE GRAPHS" REWRITE:
    ///   - There is no longer a single fixed AudioSourceNode anchor seeded
    ///     with one externally-decoded `content` buffer. An Audio-domain
    ///     graph can have ANY NUMBER of InputNodes (MediaAudioSourceNode/
    ///     ToneGeneratorInputNode/TimelineAudioInputNode), each already
    ///     resolved to a raw AudioBuffer by the caller (AudioMixer — see its
    ///     own remarks on per-InputNode-type dispatch) and handed in here as
    ///     `resolvedInputs`, keyed by each InputNode's own Id. This evaluator
    ///     seeds the (NodeId,"Audio") cache from that dictionary for every
    ///     InputNode encountered in topological order, then walks the rest
    ///     of the graph exactly as before.
    ///   - GainNode/AudioMixNode's optional Value-typed modulation ports
    ///     (see AudioEffectNodes.cs's own remarks) are resolved via
    ///     ValueGraphEvaluator — the SAME shared Value-chain walker
    ///     EffectGraphEvaluatorSk uses for MergeNode's "MixModulation" —
    ///     evaluated once per automation block (same cadence as every other
    ///     time-varying parameter here) and MULTIPLIED against that block's
    ///     own keyframed value, exactly like MergeNode's own Mix handling.
    ///   - ValueConstantNode/MathNode contribute nothing to the Audio cache
    ///     directly — resolved on demand wherever a consuming node's
    ///     modulation port is actually connected, same as the video side.
    ///
    /// TIME-VARYING PARAMETERS: an Animatable&lt;float&gt; is evaluated once
    /// every AutomationBlockFrames samples (not per-sample) and linearly
    /// interpolated between block boundaries, so automation still moves
    /// smoothly rather than stepping.
    /// </summary>
    internal static class AudioEffectGraphEvaluator
    {
        //~5ms at 48kHz — small enough that automation feels continuous,
        //large enough that per-block Animatable.Evaluate calls are not a
        //meaningful per-sample cost.
        private const int AutomationBlockFrames = 256;
 
        /// <summary>
        /// Runs the whole graph for one clip's ENTIRE decoded audio content
        /// (unlike the video evaluator, which runs once per rendered video
        /// frame, an audio clip's whole buffer is evaluated in a single
        /// pass — clip-relative time starts at 0 at content's first frame).
        /// `resolvedInputs` must contain one entry per InputNode in
        /// `graph.Nodes` (keyed by that node's own Id) — see AudioMixer's own
        /// per-InputNode-type resolution. Returns the AudioOutputNode's
        /// resolved buffer.
        /// </summary>
        public static AudioBuffer Evaluate(EffectGraph graph, IReadOnlyDictionary<Guid, AudioBuffer> resolvedInputs)
        {
            if (graph.Domain != EffectDomain.Audio)
                throw new InvalidOperationException("AudioEffectGraphEvaluator requires an Audio-domain EffectGraph.");
 
            List<EffectNode> order = EffectGraphTopology.Order(graph);
 
            var buffers = new Dictionary<(Guid, string), AudioBuffer>();
 
            //sample rate/channels for RenderAutomation's own block-time math —
            //taken from whichever InputNode buffer is seen first; every
            //resolved input at this point already shares one sample rate/
            //channel count (AudioMixer fits every input to the same target
            //before handing them to this evaluator)
            int sampleRate = 48000;
            int channels = 2;
            bool sampleFormatKnown = false;
 
            AudioBuffer? result = null;
 
            foreach (EffectNode node in order)
            {
                if (node is InputNode)
                {
                    if (!resolvedInputs.TryGetValue(node.Id, out AudioBuffer? content))
                        throw new InvalidOperationException(
                            $"No resolved content supplied for {node.GetType().Name} ({node.Id}).");
 
                    if (!sampleFormatKnown)
                    {
                        sampleRate = content.SampleRate;
                        channels = content.Channels;
                        sampleFormatKnown = true;
                    }
 
                    buffers[(node.Id, "Audio")] = content;
                    continue;
                }
 
                if (ReferenceEquals(node, graph.Output))
                {
                    result = Require(graph, node, "Audio", buffers);
                    continue;
                }
 
                switch (node)
                {
                    case GainNode gain:
                    {
                        AudioBuffer upstream = Require(graph, node, "Audio", buffers);
 
                        if (!gain.Enabled) { buffers[(node.Id, "Audio")] = upstream; break; }
 
                        float?[] modulation = RenderValueModulation(
                            graph, node, "Modulation", upstream.FrameCount, upstream.SampleRate);
 
                        buffers[(node.Id, "Audio")] = ApplyGain(upstream, gain.Gain, modulation);
                        break;
                    }
 
                    case EQNode eq:
                    {
                        AudioBuffer upstream = Require(graph, node, "Audio", buffers);
                        buffers[(node.Id, "Audio")] = eq.Enabled
                            ? ApplyEq(upstream, eq.Bands)
                            : upstream;
                        break;
                    }
 
                    case CompressorNode compressor:
                    {
                        AudioBuffer upstream = Require(graph, node, "Audio", buffers);
                        buffers[(node.Id, "Audio")] = compressor.Enabled
                            ? ApplyCompressor(upstream, compressor)
                            : upstream;
                        break;
                    }
 
                    case AudioMixNode mix:
                    {
                        AudioBuffer a = Require(graph, node, "A", buffers);
                        AudioBuffer b = Require(graph, node, "B", buffers);
                        buffers[(node.Id, "Result")] = ApplyMix(graph, mix, a, b);
                        break;
                    }
 
                    //Image/Mask-domain nodes never appear in an Audio graph —
                    //EffectGraph.AddNode already rejects a domain mismatch at
                    //edit time. ValueConstantNode/MathNode carry no Audio
                    //output at all — resolved on demand by ValueGraphEvaluator
                    //wherever a consuming node's optional modulation port is
                    //actually connected (see GainNode/AudioMixNode above), not
                    //through this cache.
                    case ValueConstantNode:
                    case MathNode:
                        break;
 
                    default:
                        throw new NotSupportedException(
                            $"AudioEffectGraphEvaluator has no dispatch for {node.GetType().Name}.");
                }
            }
 
            _ = sampleRate; _ = channels; //reserved for a future all-silent-graph fallback; unused for now
 
            return result ?? throw new InvalidOperationException(
                "EffectGraph's AudioOutputNode has no incoming connection.");
        }
 
        // -----------------------------------------------------------
        // Graph walking helpers
        // -----------------------------------------------------------
 
        private static AudioBuffer? Resolve(
            EffectGraph graph, EffectNode node, string portName, Dictionary<(Guid, string), AudioBuffer> cache)
        {
            Connection? c = graph.Connections.FirstOrDefault(x => x.ToNodeId == node.Id && x.ToPort == portName);
            if (c == null) return null;
            return cache.TryGetValue((c.FromNodeId, c.FromPort), out AudioBuffer? buf) ? buf : null;
        }
 
        private static AudioBuffer Require(
            EffectGraph graph, EffectNode node, string portName, Dictionary<(Guid, string), AudioBuffer> cache) =>
            Resolve(graph, node, portName, cache)
            ?? throw new InvalidOperationException(
                $"{node.GetType().Name}'s '{portName}' input has no incoming connection.");
 
        /// <summary>
        /// Evaluates `param` once every AutomationBlockFrames samples across
        /// `frameCount` frames (clip-relative time, second 0 == first frame),
        /// linearly interpolated between block boundaries.
        /// </summary>
        private static float[] RenderAutomation(Animatable<float> param, int frameCount, int sampleRate)
        {
            var values = new float[frameCount];
            if (frameCount == 0) return values;
 
            int blockCount = (frameCount + AutomationBlockFrames - 1) / AutomationBlockFrames + 1;
            var blockValues = new float[blockCount];
            for (int b = 0; b < blockCount; b++)
            {
                TimeSpan t = TimeSpan.FromSeconds((double)(b * AutomationBlockFrames) / sampleRate);
                blockValues[b] = param.Evaluate(t);
            }
 
            for (int i = 0; i < frameCount; i++)
            {
                int block = i / AutomationBlockFrames;
                float fraction = (i % AutomationBlockFrames) / (float)AutomationBlockFrames;
                values[i] = blockValues[block] + (blockValues[block + 1] - blockValues[block]) * fraction;
            }
 
            return values;
        }
 
        /// <summary>
        /// Same per-block cadence as RenderAutomation, but for an optional
        /// Value-typed modulation input port — one entry per frame, null
        /// throughout when the port has nothing connected (the common case,
        /// meaning "no modulation, use the node's own keyframed value as-is").
        /// </summary>
        private static float?[] RenderValueModulation(
            EffectGraph graph, EffectNode node, string portName, int frameCount, int sampleRate)
        {
            var values = new float?[frameCount];
            if (frameCount == 0) return values;
 
            int blockCount = (frameCount + AutomationBlockFrames - 1) / AutomationBlockFrames + 1;
            var blockValues = new float?[blockCount];
            bool anyConnected = false;
 
            for (int b = 0; b < blockCount; b++)
            {
                TimeSpan t = TimeSpan.FromSeconds((double)(b * AutomationBlockFrames) / sampleRate);
                float? v = ValueGraphEvaluator.TryEvaluateConnectedInput(graph, node, portName, t);
                blockValues[b] = v;
                if (v.HasValue) anyConnected = true;
            }
 
            if (!anyConnected) return values; //stays all-null
 
            for (int i = 0; i < frameCount; i++)
            {
                int block = i / AutomationBlockFrames;
                float fraction = (i % AutomationBlockFrames) / (float)AutomationBlockFrames;
 
                float a = blockValues[block] ?? 1f;
                float b2 = blockValues[block + 1] ?? 1f;
 
                values[i] = a + ((b2 - a) * fraction);
            }
 
            return values;
        }
 
        // -----------------------------------------------------------
        // Node processors
        // -----------------------------------------------------------
 
        /// <summary>
        /// GainNode's optional "Modulation" Value input MULTIPLIES against
        /// Gain's own keyframed value when connected — see AudioEffectNodes.cs's
        /// own remarks — rather than replacing it, so an author keeps Gain's
        /// own curve and layers a procedurally-computed modulation on top.
        /// </summary>
        private static AudioBuffer ApplyGain(AudioBuffer input, Animatable<float> gain, float?[] modulation)
        {
            float[] curve = RenderAutomation(gain, input.FrameCount, input.SampleRate);
            var output = new float[input.Samples.Length];
 
            for (int frame = 0; frame < input.FrameCount; frame++)
            {
                float g = curve[frame] * (modulation[frame] ?? 1f);
                int baseIdx = frame * input.Channels;
                for (int ch = 0; ch < input.Channels; ch++)
                    output[baseIdx + ch] = input.Samples[baseIdx + ch] * g;
            }
 
            return new AudioBuffer(input.SampleRate, input.Channels, output);
        }
 
        /// <summary>
        /// Chains a peaking biquad per EQBand, in list order, over every
        /// channel independently. Coefficients are recomputed every
        /// AutomationBlockFrames samples from that block's evaluated
        /// Frequency/Gain/Q — the biquad's own state (x1/x2/y1/y2) persists
        /// continuously across block boundaries so a coefficient change
        /// doesn't introduce a discontinuity in the filter's memory, only in
        /// its response.
        /// </summary>
        private static AudioBuffer ApplyEq(AudioBuffer input, List<EQBand> bands)
        {
            float[] output = (float[])input.Samples.Clone();
 
            foreach (EQBand band in bands)
            {
                output = ApplyPeakingBand(
                    new AudioBuffer(input.SampleRate, input.Channels, output), band).Samples;
            }
 
            return new AudioBuffer(input.SampleRate, input.Channels, output);
        }
 
        private static AudioBuffer ApplyPeakingBand(AudioBuffer input, EQBand band)
        {
            int frameCount = input.FrameCount;
            int channels = input.Channels;
            int sampleRate = input.SampleRate;
 
            float[] freqCurve = RenderAutomation(band.FrequencyHz, frameCount, sampleRate);
            float[] gainCurve = RenderAutomation(band.GainDb, frameCount, sampleRate);
            float[] qCurve = RenderAutomation(band.Q, frameCount, sampleRate);
 
            var output = new float[input.Samples.Length];
 
            //per-channel biquad state (Direct Form 1)
            var x1 = new double[channels];
            var x2 = new double[channels];
            var y1 = new double[channels];
            var y2 = new double[channels];
 
            (double b0, double b1, double b2, double a1, double a2) coeffs = default;
 
            for (int frame = 0; frame < frameCount; frame++)
            {
                if (frame % AutomationBlockFrames == 0)
                {
                    double freq = Math.Clamp(freqCurve[frame], 10.0, sampleRate / 2.0 - 10.0);
                    double gainDb = gainCurve[frame];
                    double q = Math.Max(0.05, qCurve[frame]);
 
                    coeffs = ComputePeakingCoefficients(freq, gainDb, q, sampleRate);
                }
 
                int baseIdx = frame * channels;
                for (int ch = 0; ch < channels; ch++)
                {
                    double x0 = input.Samples[baseIdx + ch];
                    double y0 = coeffs.b0 * x0 + coeffs.b1 * x1[ch] + coeffs.b2 * x2[ch]
                                - coeffs.a1 * y1[ch] - coeffs.a2 * y2[ch];
 
                    x2[ch] = x1[ch]; x1[ch] = x0;
                    y2[ch] = y1[ch]; y1[ch] = y0;
 
                    output[baseIdx + ch] = (float)y0;
                }
            }
 
            return new AudioBuffer(sampleRate, channels, output);
        }
 
        /// <summary>RBJ Audio EQ Cookbook peaking-filter coefficients, normalized by a0.</summary>
        private static (double b0, double b1, double b2, double a1, double a2) ComputePeakingCoefficients(
            double freq, double gainDb, double q, int sampleRate)
        {
            double a = Math.Pow(10, gainDb / 40.0);
            double w0 = 2 * Math.PI * freq / sampleRate;
            double alpha = Math.Sin(w0) / (2 * q);
            double cosW0 = Math.Cos(w0);
 
            double b0 = 1 + alpha * a;
            double b1 = -2 * cosW0;
            double b2 = 1 - alpha * a;
            double a0 = 1 + alpha / a;
            double a1 = -2 * cosW0;
            double a2 = 1 - alpha / a;
 
            return (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
        }
 
        /// <summary>
        /// Feed-forward dynamics compressor: a dB-domain envelope follower
        /// (attack/release smoothed) feeding a static threshold/ratio
        /// knee-less gain-reduction curve, plus makeup gain.
        /// </summary>
        private static AudioBuffer ApplyCompressor(AudioBuffer input, CompressorNode node)
        {
            int frameCount = input.FrameCount;
            int channels = input.Channels;
            int sampleRate = input.SampleRate;
 
            float[] thresholdCurve = RenderAutomation(node.Threshold, frameCount, sampleRate);
            float[] ratioCurve = RenderAutomation(node.Ratio, frameCount, sampleRate);
            float[] attackCurve = RenderAutomation(node.AttackMs, frameCount, sampleRate);
            float[] releaseCurve = RenderAutomation(node.ReleaseMs, frameCount, sampleRate);
            float[] makeupCurve = RenderAutomation(node.MakeupGainDb, frameCount, sampleRate);
 
            var output = new float[input.Samples.Length];
 
            const double floorDb = -120.0;
            double envelopeDb = floorDb;
 
            for (int frame = 0; frame < frameCount; frame++)
            {
                int baseIdx = frame * channels;
 
                double peak = 0.0;
                for (int ch = 0; ch < channels; ch++)
                    peak = Math.Max(peak, Math.Abs((double)input.Samples[baseIdx + ch]));
 
                double inputDb = peak <= 1e-9 ? floorDb : 20.0 * Math.Log10(peak);
 
                double attackMs = Math.Max(0.01, attackCurve[frame]);
                double releaseMs = Math.Max(0.01, releaseCurve[frame]);
 
                double coeff = inputDb > envelopeDb
                    ? Math.Exp(-1.0 / (sampleRate * (attackMs / 1000.0)))
                    : Math.Exp(-1.0 / (sampleRate * (releaseMs / 1000.0)));
 
                envelopeDb = coeff * envelopeDb + (1 - coeff) * inputDb;
 
                double threshold = thresholdCurve[frame];
                double ratio = Math.Max(1.0, ratioCurve[frame]);
 
                double gainReductionDb = envelopeDb > threshold
                    ? (threshold - envelopeDb) * (1.0 - 1.0 / ratio)
                    : 0.0;
 
                double totalGainDb = gainReductionDb + makeupCurve[frame];
                double gainLinear = Math.Pow(10, totalGainDb / 20.0);
 
                for (int ch = 0; ch < channels; ch++)
                    output[baseIdx + ch] = (float)(input.Samples[baseIdx + ch] * gainLinear);
            }
 
            return new AudioBuffer(sampleRate, channels, output);
        }
 
        /// <summary>
        /// Weighted sum of two branches. MixA/MixB's optional
        /// "MixAModulation"/"MixBModulation" Value inputs multiply against
        /// their own keyframed weights when connected, same convention as
        /// GainNode's Modulation above and MergeNode's MixModulation on the
        /// video side. The shorter of the two branches is treated as silence
        /// past its own end, so mismatched branch lengths don't throw.
        /// </summary>
        private static AudioBuffer ApplyMix(EffectGraph graph, AudioMixNode node, AudioBuffer a, AudioBuffer b)
        {
            int channels = a.Channels;
            if (b.Channels != channels)
                throw new InvalidOperationException("AudioMixNode's two branches have mismatched channel counts.");
 
            int frameCount = Math.Max(a.FrameCount, b.FrameCount);
            int sampleRate = a.SampleRate;
 
            AudioBuffer aFit = a.FrameCount == frameCount ? a : a.Slice(0, frameCount);
            AudioBuffer bFit = b.FrameCount == frameCount ? b : b.Slice(0, frameCount);
 
            float[] mixACurve = RenderAutomation(node.MixA, frameCount, sampleRate);
            float[] mixBCurve = RenderAutomation(node.MixB, frameCount, sampleRate);
 
            float?[] modA = RenderValueModulation(graph, node, "MixAModulation", frameCount, sampleRate);
            float?[] modB = RenderValueModulation(graph, node, "MixBModulation", frameCount, sampleRate);
 
            var output = new float[frameCount * channels];
 
            for (int frame = 0; frame < frameCount; frame++)
            {
                float wa = mixACurve[frame] * (modA[frame] ?? 1f);
                float wb = mixBCurve[frame] * (modB[frame] ?? 1f);
                int baseIdx = frame * channels;
 
                for (int ch = 0; ch < channels; ch++)
                {
                    output[baseIdx + ch] =
                        aFit.Samples[baseIdx + ch] * wa + bFit.Samples[baseIdx + ch] * wb;
                }
            }
 
            return new AudioBuffer(sampleRate, channels, output);
        }
    }
}
 