using System;

namespace EditSharp.Audio.Dsp
{
    /// <summary>In-place iterative radix-2 complex FFT over separate real and imaginary arrays.</summary>
    internal sealed class Fft
    {
        private readonly int _size;
        private readonly int[] _reversed;
        private readonly double[] _cos;
        private readonly double[] _sin;

        public Fft(int size)
        {
            if (size < 2 || (size & (size - 1)) != 0)
                throw new ArgumentException("FFT size must be a power of two.", nameof(size));

            _size = size;
            _reversed = new int[size];
            int bits = (int)Math.Log2(size);
            for (int i = 0; i < size; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
                _reversed[i] = r;
            }

            _cos = new double[size / 2];
            _sin = new double[size / 2];
            for (int i = 0; i < size / 2; i++)
            {
                _cos[i] = Math.Cos(2 * Math.PI * i / size);
                _sin[i] = Math.Sin(2 * Math.PI * i / size);
            }
        }

        /// <summary>Forward transform; `inverse` runs the inverse and scales by 1/size.</summary>
        public void Transform(double[] re, double[] im, bool inverse = false)
        {
            for (int i = 0; i < _size; i++)
            {
                int j = _reversed[i];
                if (j > i)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }

            double sign = inverse ? 1 : -1;

            for (int length = 2; length <= _size; length <<= 1)
            {
                int half = length / 2, stride = _size / length;
                for (int start = 0; start < _size; start += length)
                {
                    for (int k = 0; k < half; k++)
                    {
                        double wr = _cos[k * stride], wi = sign * _sin[k * stride];
                        int a = start + k, b = a + half;
                        double tr = re[b] * wr - im[b] * wi;
                        double ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                    }
                }
            }

            if (inverse)
            {
                for (int i = 0; i < _size; i++) { re[i] /= _size; im[i] /= _size; }
            }
        }
    }
}
