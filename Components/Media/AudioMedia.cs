using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EditSharp.Video;

namespace EditSharp.Components.Media
{
    /// <summary>The audio of a media file on disk: an audio file, or a video file's soundtrack.</summary>
    /// <remarks>
    /// It's streamed through ffmpeg, in the file's own time. A file with no audio
    /// stream plays silence for its length.
    /// <para>
    /// A kind of sound that isn't a plain file (an instrument plugin) derives from
    /// this and overrides how the length is found and how readers are opened.
    /// </para>
    /// </remarks>
    [MediaKind("media-audio", DisplayName = "Audio")]
    public class AudioMedia : IMedia
    {
        /// <inheritdoc/>
        public override AudioMedia Duplicate() => (AudioMedia)base.Duplicate();

        /// <inheritdoc/>
        /// <remarks>Answers from the probe cache; a file not probed yet starts its probe in the background.</remarks>
        public override bool TryGetNaturalLength(out Time? length)
        {
            length = null;

            if (!MediaProbe.TryGetCached(Path, out MediaInfo info))
            {
                if (File.Exists(Path)) _ = MediaProbe.ProbeCachedAsync(Path).ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                return false;
            }

            length = info.Duration;
            return true;
        }

        /// <summary>How long the file is, from probing it.</summary>
        /// <param name="ct">Cancels waiting for the probe.</param>
        /// <returns>The file's length; null if it has none.</returns>
        /// <exception cref="SourceUnavailableException"><see cref="SourceUnavailableReason.NoMedia"/> when no file is chosen; <see cref="SourceUnavailableReason.MediaOffline"/> when it's missing; <see cref="SourceUnavailableReason.DecodeError"/> when it can't be probed.</exception>
        public override async Task<Time?> GetNaturalLengthAsync(CancellationToken ct = default) =>
            (await ProbeAsync(Path, ct)).Duration;

        /// <summary>The sample rate peaks are measured at.</summary>
        public const int PeakSampleRate = 48000;

        /// <summary>Peaks of a stretch of the file, for drawing a waveform; nothing stays open afterwards.</summary>
        /// <param name="start">Where the stretch starts, in the file's own time.</param>
        /// <param name="duration">How long the stretch is.</param>
        /// <param name="buckets">How many equal buckets to split it into.</param>
        /// <param name="ct">Cancels reading.</param>
        /// <returns>The peaks, one bucket per column; buckets past the end of the file are 0.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> isn't positive, or <paramref name="buckets"/> isn't positive.</exception>
        /// <exception cref="SourceUnavailableException">The file can't provide samples.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
        public virtual async Task<AudioPeaks> GetPeaksAsync(Time start, Time duration, int buckets, CancellationToken ct = default)
        {
            if (duration <= Time.Zero) throw new ArgumentOutOfRangeException(nameof(duration), "duration must be positive.");
            if (buckets <= 0) throw new ArgumentOutOfRangeException(nameof(buckets), "buckets must be positive.");

            using IPreparedAudioSource prepared = await PrepareAsync(ct).ConfigureAwait(false);

            return await Task.Run(() =>
            {
                using IAudioSampleReader reader = prepared.OpenReader(new AudioReaderOptions(PeakSampleRate, 1, start < Time.Zero ? Time.Zero : start));

                long total = duration.ToSamples(PeakSampleRate, Rounding.Nearest);
                var peaks = new PeakAccumulator(buckets, total);
                float[] block = new float[8192];
                long read = 0;

                while (read < total)
                {
                    ct.ThrowIfCancellationRequested();

                    int wanted = (int)Math.Min(block.Length, total - read);
                    int got;
                    try { got = reader.Read(block.AsSpan(0, wanted)); }
                    catch (SourceUnavailableException e) when (e.Reason == SourceUnavailableReason.EndOfSource) { break; }

                    if (got <= 0) break;
                    peaks.Add(block.AsSpan(0, got), read, 1);
                    read += got;
                }

                return peaks.Result();
            }, ct).ConfigureAwait(false);
        }

        /// <summary>Readies the file for one session: probes it.</summary>
        /// <param name="ct">Cancels preparing.</param>
        /// <returns>The prepared media, whose readers take time in the file; the caller disposes it.</returns>
        /// <exception cref="SourceUnavailableException">The file can't provide samples.</exception>
        internal virtual async Task<IPreparedAudioSource> PrepareAsync(CancellationToken ct = default)
        {
            string path = Path;
            MediaInfo info = await ProbeAsync(path, ct);
            return new PreparedAudioFile(path, info);
        }

        private static async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(path))
                throw new SourceUnavailableException(SourceUnavailableReason.NoMedia, "No file is chosen.");

            try
            {
                return await MediaProbe.ProbeCachedAsync(path).WaitAsync(ct);
            }
            catch (FileNotFoundException ex)
            {
                throw new SourceUnavailableException(SourceUnavailableReason.MediaOffline, $"'{path}' is missing.", ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new SourceUnavailableException(SourceUnavailableReason.DecodeError, $"Could not probe '{path}'.", ex);
            }
        }
    }
}
