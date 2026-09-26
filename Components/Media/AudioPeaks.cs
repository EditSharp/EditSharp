using System;

namespace EditSharp.Components.Media
{
    /// <summary>The quietest and loudest sample in each of a row of equal time buckets, every channel mixed to one.</summary>
    /// <remarks>A bucket the audio never reached (past the end of the material) holds 0 in both.</remarks>
    public sealed class AudioPeaks
    {
        /// <summary>The lowest sample in each bucket, -1 to 1.</summary>
        public float[] Min { get; }

        /// <summary>The highest sample in each bucket, -1 to 1.</summary>
        public float[] Max { get; }

        /// <summary>How many buckets there are.</summary>
        public int Count => Min.Length;

        internal AudioPeaks(float[] min, float[] max)
        {
            Min = min;
            Max = max;
        }
    }

    /// <summary>Folds interleaved sample blocks into <see cref="AudioPeaks"/> buckets.</summary>
    internal sealed class PeakAccumulator(int buckets, long totalFrames)
    {
        private readonly float[] _min = new float[buckets];
        private readonly float[] _max = new float[buckets];
        private readonly bool[] _touched = new bool[buckets];

        /// <summary>Adds a block whose first frame is `firstFrame` of the whole, mixing `channels` to one.</summary>
        public void Add(ReadOnlySpan<float> samples, long firstFrame, int channels)
        {
            int frames = samples.Length / channels;

            for (int f = 0; f < frames; f++)
            {
                long frame = firstFrame + f;
                if (frame >= totalFrames) return;

                int bucket = (int)(frame * buckets / totalFrames);
                float sum = 0f;
                for (int c = 0; c < channels; c++) sum += samples[f * channels + c];
                float sample = sum / channels;

                if (!_touched[bucket])
                {
                    _touched[bucket] = true;
                    _min[bucket] = sample;
                    _max[bucket] = sample;
                }
                else
                {
                    if (sample < _min[bucket]) _min[bucket] = sample;
                    if (sample > _max[bucket]) _max[bucket] = sample;
                }
            }
        }

        public AudioPeaks Result() => new(_min, _max);
    }
}
