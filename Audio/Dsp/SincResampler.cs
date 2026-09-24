using System;
using EditSharp.Audio.Engine;

namespace EditSharp.Audio.Dsp
{
    /// <summary>
    /// Varispeed by band-limited interpolation: each output frame is the
    /// content signal reconstructed at a fractional position with a
    /// Kaiser-windowed sinc (16 zero crossings per side, beta 8.6). Speeding up
    /// lowers the cutoff by the speed so content above the new Nyquist
    /// doesn't alias; the kernel widens with it, up to 16x.
    /// </summary>
    internal static class SincResampler
    {
        private const int ZeroCrossings = 16;
        private const double Beta = 8.6;
        private const double MinCutoff = 1.0 / 16;
        private const int TableSize = 4096;

        //Kaiser window over |x| in [0, 1]
        private static readonly double[] Kaiser = BuildKaiser();

        public static void Render(ContentWindow window, double start, double step, int frames, Span<float> output)
        {
            int channels = window.Channels;
            double cutoff = Math.Max(MinCutoff, step > 1 ? 1.0 / step : 1.0);
            int half = (int)Math.Ceiling(ZeroCrossings / cutoff);

            double last = start + step * (frames - 1);
            window.Ensure((long)Math.Floor(Math.Min(start, last)) - half, (long)Math.Floor(Math.Max(start, last)) + half + 1);

            Span<double> sum = stackalloc double[channels];

            for (int i = 0; i < frames; i++)
            {
                double position = start + step * i;
                long center = (long)Math.Floor(position);
                double fraction = position - center;

                sum.Clear();
                double weight = 0;

                for (int j = -half + 1; j <= half; j++)
                {
                    double distance = j - fraction;
                    double h = cutoff * Sinc(distance * cutoff) * Window(Math.Abs(distance) / half);
                    weight += h;

                    for (int ch = 0; ch < channels; ch++) sum[ch] += window.At(center + j, ch) * h;
                }

                //normalizing by the kernel's own sum keeps DC exact at every fraction
                double scale = weight != 0 ? 1.0 / weight : 0;
                for (int ch = 0; ch < channels; ch++) output[i * channels + ch] = (float)(sum[ch] * scale);
            }
        }

        private static double Sinc(double x) => x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);

        private static double Window(double x)
        {
            if (x >= 1) return 0;
            double position = x * (TableSize - 1);
            int index = (int)position;
            double fraction = position - index;
            return Kaiser[index] + (Kaiser[Math.Min(index + 1, TableSize - 1)] - Kaiser[index]) * fraction;
        }

        private static double[] BuildKaiser()
        {
            var table = new double[TableSize];
            double denominator = BesselI0(Beta);
            for (int i = 0; i < TableSize; i++)
            {
                double x = i / (double)(TableSize - 1);
                table[i] = BesselI0(Beta * Math.Sqrt(1 - x * x)) / denominator;
            }
            return table;
        }

        private static double BesselI0(double x)
        {
            double sum = 1, term = 1, quarter = x * x / 4;
            for (int k = 1; k < 50; k++)
            {
                term *= quarter / (k * k);
                sum += term;
                if (term < sum * 1e-12) break;
            }
            return sum;
        }
    }
}
