using System;
 
namespace EditSharp.Audio
{
    /// <summary>
    /// In-memory interleaved float32 PCM — the unit every stage of the new
    /// audio pipeline (PcmAudioDecoder -> AudioGraphEvaluator ->
    /// AudioMixer) passes around, replacing the old ffmpeg-filtergraph
    /// string-building approach (adelay/amix/volume filter lines) entirely.
    /// See PcmAudioDecoder's and AudioMixer's own remarks for why: the user
    /// explicitly asked for a REAL audio graph walker (mirroring
    /// ImageGraphEvaluator's topological-order dispatch for video), and a
    /// real per-node evaluator needs actual samples to operate on, not
    /// another string of ffmpeg filter syntax.
    ///
    /// Samples are interleaved (LRLRLR... for stereo), one float per sample
    /// in the conventional -1..1 range (not clamped here — clipping, if any,
    /// happens only at final output conversion, so intermediate gain stages
    /// stacking above 1.0 don't silently lose headroom mid-graph).
    /// </summary>
    internal sealed class AudioBuffer
    {
        public int SampleRate { get; }
        public int Channels { get; }
 
        /// <summary>Interleaved samples, length == FrameCount * Channels.</summary>
        public float[] Samples { get; }
 
        public int FrameCount => Samples.Length / Channels;
 
        public AudioBuffer(int sampleRate, int channels, float[] samples)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
            if (samples.Length % channels != 0)
                throw new ArgumentException("samples.Length must be a whole multiple of channels.");
 
            SampleRate = sampleRate;
            Channels = channels;
            Samples = samples;
        }
 
        public static AudioBuffer Silence(int sampleRate, int channels, int frameCount) =>
            new(sampleRate, channels, new float[Math.Max(0, frameCount) * channels]);
 
        public static int FramesForDuration(TimeSpan duration, int sampleRate) =>
            Math.Max(0, (int)Math.Round(duration.TotalSeconds * sampleRate));
 
        /// <summary>
        /// Returns a NEW buffer with exactly `targetFrames` frames, built from this
        /// buffer's content:
        ///   - if this buffer already has enough frames, the excess is trimmed;
        ///   - otherwise, if `allowLoop` is true, the content is tiled (repeated
        ///     from the start) to fill the remainder — mirrors the old
        ///     -stream_loop -1 behaviour for a source with no explicit trim range;
        ///   - otherwise the LAST FRAME is held (repeated) for the remainder —
        ///     mirrors the old tpad=stop_mode=clone freeze-frame behaviour for a
        ///     source with an explicit Start/Duration that can't be looped.
        /// A zero-length source buffer (e.g. a genuinely empty decode) fits by
        /// returning silence — there is nothing to loop or hold.
        /// </summary>
        public AudioBuffer FitToFrames(int targetFrames, bool allowLoop)
        {
            targetFrames = Math.Max(0, targetFrames);
 
            if (FrameCount == targetFrames) return this;
 
            var result = new float[targetFrames * Channels];
 
            if (FrameCount == 0)
                return new AudioBuffer(SampleRate, Channels, result); //nothing to source from — silence
 
            if (FrameCount >= targetFrames)
            {
                Array.Copy(Samples, result, result.Length);
                return new AudioBuffer(SampleRate, Channels, result);
            }
 
            //shorter than target: copy what we have, then either tile or hold
            Array.Copy(Samples, result, Samples.Length);
 
            if (allowLoop)
            {
                int written = FrameCount;
                while (written < targetFrames)
                {
                    int toCopy = Math.Min(FrameCount, targetFrames - written);
                    Array.Copy(Samples, 0, result, written * Channels, toCopy * Channels);
                    written += toCopy;
                }
            }
            else
            {
                //hold the last frame (freeze-frame equivalent for audio)
                int lastFrameOffset = (FrameCount - 1) * Channels;
                for (int frame = FrameCount; frame < targetFrames; frame++)
                {
                    Array.Copy(Samples, lastFrameOffset, result, frame * Channels, Channels);
                }
            }
 
            return new AudioBuffer(SampleRate, Channels, result);
        }
 
        /// <summary>
        /// Returns a NEW buffer with exactly `targetFrames` frames holding
        /// this buffer's whole content, stretched or squeezed to fit by
        /// linear interpolation — plain varispeed, so pitch follows tempo.
        /// This is how Clip.Speed reaches audio: content decoded for the
        /// clip's content duration is resampled to its timeline duration.
        /// An empty buffer resamples to silence.
        /// </summary>
        public AudioBuffer Resample(int targetFrames)
        {
            targetFrames = Math.Max(0, targetFrames);

            if (FrameCount == targetFrames) return this;
            if (FrameCount == 0 || targetFrames == 0) return Silence(SampleRate, Channels, targetFrames);

            var result = new float[targetFrames * Channels];
            double step = (double)FrameCount / targetFrames;

            for (int frame = 0; frame < targetFrames; frame++)
            {
                double position = frame * step;
                int index = Math.Min((int)position, FrameCount - 1);
                int next = Math.Min(index + 1, FrameCount - 1);
                float fraction = (float)(position - index);

                int from = index * Channels;
                int to = next * Channels;
                int into = frame * Channels;

                for (int ch = 0; ch < Channels; ch++)
                    result[into + ch] = Samples[from + ch] + (Samples[to + ch] - Samples[from + ch]) * fraction;
            }

            return new AudioBuffer(SampleRate, Channels, result);
        }

        /// <summary>Multiplies every sample by a fixed gain, in place.</summary>
        public void ApplyGain(float gain)
        {
            if (Math.Abs(gain - 1f) < 1e-6f) return;
            for (int i = 0; i < Samples.Length; i++) Samples[i] *= gain;
        }
 
        /// <summary>
        /// Additively mixes `source` into this buffer starting at
        /// `destFrameOffset` (which may be negative or run past this buffer's
        /// end — only the overlapping region is actually mixed, so a clip
        /// placed with Start before 0 or extending past the timeline's own
        /// length doesn't need special-casing by the caller).
        /// </summary>
        public void MixFrom(AudioBuffer source, int destFrameOffset)
        {
            if (source.Channels != Channels)
                throw new InvalidOperationException(
                    $"Channel count mismatch mixing audio: {source.Channels} into {Channels}.");
 
            int srcStartFrame = destFrameOffset < 0 ? -destFrameOffset : 0;
            int dstStartFrame = Math.Max(destFrameOffset, 0);
 
            int framesAvailableSrc = source.FrameCount - srcStartFrame;
            int framesAvailableDst = FrameCount - dstStartFrame;
            int framesToMix = Math.Min(framesAvailableSrc, framesAvailableDst);
 
            if (framesToMix <= 0) return;
 
            int srcOffset = srcStartFrame * Channels;
            int dstOffset = dstStartFrame * Channels;
            int sampleCount = framesToMix * Channels;
 
            for (int i = 0; i < sampleCount; i++)
                Samples[dstOffset + i] += source.Samples[srcOffset + i];
        }
 
        /// <summary>Extracts [startFrame, startFrame+frameCount) as a new buffer, zero-padding past either end.</summary>
        public AudioBuffer Slice(int startFrame, int frameCount)
        {
            var result = new float[Math.Max(0, frameCount) * Channels];
 
            int srcStart = Math.Max(startFrame, 0);
            int srcEnd = Math.Min(startFrame + frameCount, FrameCount);
            int overlapFrames = srcEnd - srcStart;
 
            if (overlapFrames > 0)
            {
                int dstStartFrame = srcStart - startFrame;
                Array.Copy(
                    Samples, srcStart * Channels,
                    result, dstStartFrame * Channels,
                    overlapFrames * Channels);
            }
 
            return new AudioBuffer(SampleRate, Channels, result);
        }
 
        /// <summary>Converts to 16-bit signed little-endian interleaved PCM bytes (playback delivery format).</summary>
        public byte[] ToInt16Bytes()
        {
            var bytes = new byte[Samples.Length * 2];
            for (int i = 0; i < Samples.Length; i++)
            {
                float clamped = Math.Clamp(Samples[i], -1f, 1f);
                short s16 = (short)Math.Round(clamped * short.MaxValue);
                bytes[i * 2] = (byte)(s16 & 0xFF);
                bytes[i * 2 + 1] = (byte)((s16 >> 8) & 0xFF);
            }
            return bytes;
        }
 
        /// <summary>Raw 32-bit float little-endian interleaved PCM bytes (final-render mux input format).</summary>
        public byte[] ToFloat32Bytes()
        {
            var bytes = new byte[Samples.Length * 4];
            Buffer.BlockCopy(Samples, 0, bytes, 0, bytes.Length);
            return bytes;
        }
    }
}
 