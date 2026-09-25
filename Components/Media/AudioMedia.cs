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
        public override bool TryGetNaturalLength(out TimeSpan? length)
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
        public override async Task<TimeSpan?> GetNaturalLengthAsync(CancellationToken ct = default) =>
            (await ProbeAsync(Path, ct)).Duration;

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
