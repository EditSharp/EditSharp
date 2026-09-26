using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using EditSharp.Caching.Proxy;
using EditSharp.History;
using EditSharp.Video;

namespace EditSharp.Components.Media
{
    /// <summary>A video file or a still image on disk.</summary>
    /// <remarks>
    /// Which one it is comes from probing the file, and isn't saved. A still has no
    /// natural length and no proxy, and is decoded once per session. A video reads
    /// its proxy or the original according to the session's <see cref="SourceMode"/>;
    /// random access always reads the proxy. Readers work in the file's own time.
    /// <para>
    /// The file is one resource with two streams: the picture this media reads, and
    /// the soundtrack its <see cref="Audio"/> reads.
    /// </para>
    /// <para>
    /// A kind of picture that isn't a plain video file (a vector drawing, a rendered
    /// scene) derives from this and overrides how the length is found and how readers are opened.
    /// </para>
    /// </remarks>
    [MediaKind("media-video", DisplayName = "Video")]
    public class VideoMedia : IMedia
    {
        /// <summary>A video media whose <see cref="Audio"/> is the same file's soundtrack.</summary>
        public VideoMedia()
        {
            _audio = new AudioMedia { Path = "" };
        }

        AudioMedia? _audio;
        /// <summary>The file's soundtrack, as a media of its own that audio nodes can read; null when there is none to offer.</summary>
        /// <remarks>
        /// It belongs to this media: it's saved inside it, copied with it, and its
        /// <see cref="IMedia.Path"/> follows this media's. A file with no audio stream
        /// still offers it, playing silence. A kind that has no sound sets it null.
        /// </remarks>
        public AudioMedia? Audio
        {
            //a still image has no sound to offer, once the probe has said it's one
            get => _audio is not null && MediaProbe.TryGetCached(Path, out MediaInfo info) && info.IsStillImage ? null : _audio;
            set
            {
                Transaction.Set(this, ref _audio, value, static (o, v) => o._audio = v);
                if (value is not null) value.Path = Path;
            }
        }

        /// <inheritdoc/>
        protected override void PathChanged()
        {
            if (_audio is not null) _audio.Path = Path;
        }

        /// <inheritdoc/>
        public override VideoMedia Duplicate() => (VideoMedia)base.Duplicate();

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

            if (info.IsStillImage) return true;
            length = info.Duration;
            return length is not null;
        }

        /// <summary>How long the video is, from probing the file.</summary>
        /// <param name="ct">Cancels waiting for the probe.</param>
        /// <returns>The video's length; null for a still image.</returns>
        /// <exception cref="SourceUnavailableException"><see cref="SourceUnavailableReason.NoMedia"/> when no file is chosen; <see cref="SourceUnavailableReason.MediaOffline"/> when it's missing; <see cref="SourceUnavailableReason.DecodeError"/> when it can't be probed or has no duration.</exception>
        public override async Task<TimeSpan?> GetNaturalLengthAsync(CancellationToken ct = default)
        {
            MediaInfo info = await ProbeAsync(Path, ct);

            if (info.IsStillImage) return null;

            return info.Duration
                ?? throw new SourceUnavailableException(SourceUnavailableReason.DecodeError, $"'{Path}' has no readable duration.");
        }

        /// <summary>Readies the file for one session: probes it, chooses a decoder and looks for a proxy.</summary>
        /// <param name="context">What the session needs from every media it prepares.</param>
        /// <param name="ct">Cancels preparing.</param>
        /// <returns>The prepared media, whose readers take time in the file; the caller disposes it.</returns>
        /// <exception cref="SourceUnavailableException">The file can't provide frames.</exception>
        internal virtual async Task<IPreparedVideoSource> PrepareAsync(VideoPrepareContext context, CancellationToken ct = default)
        {
            string path = Path;
            MediaInfo info = await ProbeAsync(path, ct);

            if (!info.HasVideo)
                throw new SourceUnavailableException(SourceUnavailableReason.DecodeError, $"'{path}' has no video stream.");

            if (info.IsStillImage)
                return new PreparedStill(LoadImage(path), (info.Width, info.Height));

            if (info.Duration is null)
                throw new SourceUnavailableException(SourceUnavailableReason.DecodeError, $"'{path}' has no readable duration.");

            //random access reads the proxy in every mode, so look for one whatever the mode
            ProxyEntry? proxy = await TryGetProxyAsync(path, ct);

            DecodeHwAccelPlan plan = context.Mode == SourceMode.ProxiesOnly
                ? DecodeHwAccelPlan.Software
                : await FfmpegRunner.GetDecodePlanAsync(path, context.HwAccel);

            return new PreparedVideoFile(path, info, plan, context.Mode, proxy);
        }

        /// <summary>One frame, for callers that need a single picture, such as thumbnails; nothing stays open afterwards.</summary>
        /// <remarks>A still image is read directly in every mode, since it has no proxy.</remarks>
        /// <param name="time">Time in the file.</param>
        /// <param name="mode">Whether to read the proxy or the original, as a session would.</param>
        /// <param name="maxWidth">The largest width wanted; 0 for the native width.</param>
        /// <param name="maxHeight">The largest height wanted; 0 for the native height.</param>
        /// <param name="ct">Cancels reading the frame.</param>
        /// <returns>The frame; the caller disposes it.</returns>
        /// <exception cref="SourceUnavailableException">The frame can't be read; with <see cref="SourceMode.ProxiesOnly"/>, a frame with no proxy is <see cref="SourceUnavailableReason.ProxyPending"/> or <see cref="SourceUnavailableReason.ProxyMissing"/>.</exception>
        public virtual async Task<SKImage> GetFrameAtAsync(
            TimeSpan time, SourceMode mode = SourceMode.SourceOnly, int maxWidth = 0, int maxHeight = 0,
            CancellationToken ct = default)
        {
            MediaInfo info = await ProbeAsync(Path, ct);

            //stills have no proxy: every mode reads the image itself
            if (info.IsStillImage) return LoadImage(Path);

            return await VideoFrames.ReadOnceAsync(PrepareAsync, time, mode, maxWidth, maxHeight, ct);
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

        //a failed lookup just means no proxy yet; readers keep checking for one appearing
        private static async Task<ProxyEntry?> TryGetProxyAsync(string path, CancellationToken ct)
        {
            try
            {
                return await ProxyCache.TryGetAsync(path, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                EditSharpConfig.Logger.LogWarning($"Proxy lookup failed for '{path}': {ex.Message}");
                return null;
            }
        }

        internal static SKImage LoadImage(string path)
        {
            using SKData? data = SKData.Create(path);

            if (data is null)
                throw new SourceUnavailableException(
                    File.Exists(path) ? SourceUnavailableReason.DecodeError : SourceUnavailableReason.MediaOffline,
                    $"Could not read '{path}'.");

            return SKImage.FromEncodedData(data)
                ?? throw new SourceUnavailableException(SourceUnavailableReason.DecodeError, $"Could not decode image '{path}'.");
        }
    }
}
