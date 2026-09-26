using System;

namespace EditSharp.Audio.Analysis
{
    /// <summary>One frame of audio in the frequency domain: the energy in each band and the sample peak.</summary>
    /// <remarks>Nodes read their inputs' frames and write their output's through <see cref="EditSharp.Components.Nodes.Node.DescribeSpectrum"/>.</remarks>
    public sealed class SpectralFrame
    {
        /// <summary>The energy in each of the <see cref="AudioAnalysis.BandCount"/> bands; a full-scale sine totals 0.5.</summary>
        public float[] Bands { get; } = new float[AudioAnalysis.BandCount];

        /// <summary>The largest sample magnitude in the frame, 0 to 1 for unclipped audio.</summary>
        public float Peak { get; set; }

        /// <summary>The frame's total energy: the sum of its bands.</summary>
        public float Energy
        {
            get
            {
                float sum = 0f;
                foreach (float band in Bands) sum += band;
                return sum;
            }
        }

        /// <summary>The frame's RMS level, from its energy.</summary>
        public float Rms => MathF.Sqrt(Math.Max(0f, Energy));

        /// <summary>Makes this frame a copy of another.</summary>
        /// <param name="other">The frame to copy.</param>
        public void CopyFrom(SpectralFrame other)
        {
            other.Bands.AsSpan().CopyTo(Bands);
            Peak = other.Peak;
        }

        /// <summary>Makes this frame silent.</summary>
        public void Clear()
        {
            Array.Clear(Bands);
            Peak = 0f;
        }

        /// <summary>Scales every band's energy and the peak by one linear gain.</summary>
        /// <param name="gain">The gain, as an amplitude factor.</param>
        public void Scale(float gain)
        {
            float power = gain * gain;
            for (int i = 0; i < Bands.Length; i++) Bands[i] *= power;
            Peak *= Math.Abs(gain);
        }
    }

    /// <summary>What a node knows about the frame it's describing.</summary>
    /// <param name="ContentTime">The frame's time in the clip's content, at 1x, for evaluating keyframed parameters.</param>
    /// <param name="FrameSeconds">How long the frame is.</param>
    public readonly record struct SpectralContext(Time ContentTime, double FrameSeconds);
}
