using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EditSharp.Audio.Dsp;
using EditSharp.Components.Media;

namespace EditSharp.Audio.Analysis
{
    /// <summary>A media file's audio described frame by frame in the frequency domain: band energies and the sample peak of every hop.</summary>
    /// <remarks>
    /// Frames are <see cref="Hop"/> samples apart at <see cref="SampleRate"/>, channels mixed to one.
    /// Each frame holds the energy in <see cref="BandCount"/> log-spaced bands from a <see cref="Window"/>-point FFT,
    /// scaled so that a full-scale sine gives a total of 0.5 (its mean square), and the largest sample magnitude of the hop.
    /// Nodes describe their effect on these frames (<see cref="EditSharp.Components.Nodes.Node.DescribeSpectrum"/>), and
    /// <see cref="ClipSpectrum"/> folds a clip's graph over them for a waveform display that shows the processed audio.
    /// </remarks>
    public sealed class AudioAnalysis
    {
        /// <summary>The rate the audio is analysed at.</summary>
        public const int SampleRate = 48000;

        /// <summary>Samples between frames.</summary>
        public const int Hop = 512;

        /// <summary>Samples in the FFT window; a Hann window over two hops.</summary>
        public const int Window = 1024;

        /// <summary>Bands per frame.</summary>
        public const int BandCount = 32;

        /// <summary>The lowest band edge, in hertz.</summary>
        public const double LowestHz = 20.0;

        /// <summary>The highest band edge, in hertz.</summary>
        public const double HighestHz = 20000.0;

        /// <summary>Seconds one frame stands for.</summary>
        public static readonly double FrameSeconds = Hop / (double)SampleRate;

        /// <summary>The band edges, <see cref="BandCount"/> + 1 of them, log spaced from <see cref="LowestHz"/> to <see cref="HighestHz"/>.</summary>
        public static readonly double[] BandEdgesHz = MakeEdges();

        /// <summary>The geometric centre of each band, in hertz.</summary>
        public static readonly double[] BandCentresHz = MakeCentres();

        /// <summary>How many frames there are.</summary>
        public int FrameCount { get; }

        /// <summary>Band energies, frame-major: frame f's bands are at f * <see cref="BandCount"/>.</summary>
        public float[] Bands { get; }

        /// <summary>The largest sample magnitude in each frame's hop, 0 to 1.</summary>
        public float[] Peak { get; }

        /// <summary>How long the analysed audio is.</summary>
        public Time Duration => FrameTime(FrameCount);

        private AudioAnalysis(int frameCount, float[] bands, float[] peak)
        {
            FrameCount = frameCount;
            Bands = bands;
            Peak = peak;
        }

        /// <summary>The frame a moment in the audio falls in.</summary>
        /// <param name="time">Time in the audio.</param>
        /// <returns>The frame index; negative or past the end when the time is outside the audio.</returns>
        public int FrameAt(Time time) => (int)Math.Floor(time.ToSamples(SampleRate) / (double)Hop);

        /// <summary>When a frame starts.</summary>
        /// <param name="frame">The frame index.</param>
        /// <returns>The time, exactly.</returns>
        public static Time FrameTime(long frame) => Time.FromSamples(frame * Hop, SampleRate);

        /// <summary>The energies of one frame's bands.</summary>
        /// <param name="frame">The frame index.</param>
        /// <returns>The <see cref="BandCount"/> energies; all zero outside the audio.</returns>
        public ReadOnlySpan<float> BandsOf(int frame) =>
            frame < 0 || frame >= FrameCount ? Silence : Bands.AsSpan(frame * BandCount, BandCount);

        /// <summary>The peak of one frame.</summary>
        /// <param name="frame">The frame index.</param>
        /// <returns>The peak; 0 outside the audio.</returns>
        public float PeakOf(int frame) => frame < 0 || frame >= FrameCount ? 0f : Peak[frame];

        private static readonly float[] Silence = new float[BandCount];

        // ---- building ----

        /// <summary>Analyses a reader's whole output.</summary>
        /// <param name="reader">A mono reader at <see cref="SampleRate"/>, at its start.</param>
        /// <param name="progress">Receives frames analysed so far, when the length is unknown, as a count.</param>
        /// <param name="ct">Cancels the analysis.</param>
        /// <returns>The analysis.</returns>
        internal static AudioAnalysis Build(IAudioSampleReader reader, IProgress<int>? progress, CancellationToken ct)
        {
            var fft = new Fft(Window);
            double[] window = new double[Window];
            for (int i = 0; i < Window; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Window);

            int[] bandOfBin = new int[Window / 2];
            for (int k = 0; k < Window / 2; k++)
            {
                double hz = k * (double)SampleRate / Window;
                int band = -1;
                for (int b = 0; b < BandCount; b++)
                    if (hz >= BandEdgesHz[b] && hz < BandEdgesHz[b + 1]) { band = b; break; }
                bandOfBin[k] = band;
            }

            var bands = new System.Collections.Generic.List<float>();
            var peaks = new System.Collections.Generic.List<float>();

            float[] history = new float[Window];
            float[] block = new float[Hop];
            double[] re = new double[Window];
            double[] im = new double[Window];
            float[] frameBands = new float[BandCount];
            bool ended = false;
            int frames = 0;

            //a Hann window over two hops: half the energy of a rectangular one, so the scale doubles back
            double scale = 2.0 / (Window * Window) * 4.0;

            while (!ended)
            {
                ct.ThrowIfCancellationRequested();

                int got;
                try { got = reader.Read(block); }
                catch (SourceUnavailableException e) when (e.Reason == SourceUnavailableReason.EndOfSource) { got = 0; }

                if (got < Hop)
                {
                    Array.Clear(block, got, Hop - got);
                    ended = true;
                    if (got == 0 && frames > 0) break;
                }

                //slide the window on by a hop
                Array.Copy(history, Hop, history, 0, Window - Hop);
                Array.Copy(block, 0, history, Window - Hop, Hop);

                float peak = 0f;
                for (int i = 0; i < Hop; i++)
                {
                    float a = Math.Abs(block[i]);
                    if (a > peak) peak = a;
                }

                for (int i = 0; i < Window; i++) { re[i] = history[i] * window[i]; im[i] = 0.0; }
                fft.Transform(re, im);

                Array.Clear(frameBands);
                for (int k = 1; k < Window / 2; k++)
                {
                    int band = bandOfBin[k];
                    if (band < 0) continue;
                    frameBands[band] += (float)((re[k] * re[k] + im[k] * im[k]) * scale);
                }

                bands.AddRange(frameBands);
                peaks.Add(Math.Min(1f, peak));
                frames++;

                if ((frames & 255) == 0) progress?.Report(frames);
            }

            return new AudioAnalysis(frames, [.. bands], [.. peaks]);
        }

        // ---- the file format: a small header then the two arrays ----

        private const uint Magic = 0x41525345; //"ESRA"
        private const int Version = 1;

        /// <summary>Writes the analysis.</summary>
        /// <param name="stream">Where it goes.</param>
        public void Save(Stream stream)
        {
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(SampleRate);
            writer.Write(Hop);
            writer.Write(Window);
            writer.Write(BandCount);
            writer.Write(FrameCount);

            byte[] buffer = new byte[Bands.Length * sizeof(float)];
            Buffer.BlockCopy(Bands, 0, buffer, 0, buffer.Length);
            writer.Write(buffer);

            buffer = new byte[Peak.Length * sizeof(float)];
            Buffer.BlockCopy(Peak, 0, buffer, 0, buffer.Length);
            writer.Write(buffer);
        }

        /// <summary>Reads an analysis written by <see cref="Save"/>.</summary>
        /// <param name="stream">Where it comes from.</param>
        /// <returns>The analysis, or null when the stream isn't one this version reads.</returns>
        public static AudioAnalysis? Load(Stream stream)
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version) return null;
            if (reader.ReadInt32() != SampleRate || reader.ReadInt32() != Hop || reader.ReadInt32() != Window || reader.ReadInt32() != BandCount) return null;

            int frames = reader.ReadInt32();
            if (frames < 0) return null;

            float[] bands = new float[frames * BandCount];
            byte[] buffer = reader.ReadBytes(bands.Length * sizeof(float));
            if (buffer.Length != bands.Length * sizeof(float)) return null;
            Buffer.BlockCopy(buffer, 0, bands, 0, buffer.Length);

            float[] peak = new float[frames];
            buffer = reader.ReadBytes(peak.Length * sizeof(float));
            if (buffer.Length != peak.Length * sizeof(float)) return null;
            Buffer.BlockCopy(buffer, 0, peak, 0, buffer.Length);

            return new AudioAnalysis(frames, bands, peak);
        }

        private static double[] MakeEdges()
        {
            double[] edges = new double[BandCount + 1];
            double ratio = HighestHz / LowestHz;
            for (int i = 0; i <= BandCount; i++) edges[i] = LowestHz * Math.Pow(ratio, i / (double)BandCount);
            return edges;
        }

        private static double[] MakeCentres()
        {
            double[] centres = new double[BandCount];
            for (int i = 0; i < BandCount; i++) centres[i] = Math.Sqrt(BandEdgesHz[i] * BandEdgesHz[i + 1]);
            return centres;
        }

        /// <summary>The band a frequency falls in.</summary>
        /// <param name="hz">The frequency.</param>
        /// <returns>The band index; -1 outside the analysed range.</returns>
        public static int BandOf(double hz)
        {
            if (hz < LowestHz || hz >= HighestHz) return -1;
            return Math.Clamp((int)Math.Floor(Math.Log(hz / LowestHz) / Math.Log(HighestHz / LowestHz) * BandCount), 0, BandCount - 1);
        }
    }
}
