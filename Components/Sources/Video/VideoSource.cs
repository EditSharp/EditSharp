using System;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using EditSharp.Video;

namespace EditSharp.Components.Sources.Video
{
    /// <summary>A source of frames.</summary>
    /// <remarks>
    /// Reading has two steps. PrepareAsync does the slow work once per session
    /// (probing, choosing a file and decoder) and returns a prepared handle; the
    /// handle opens cheap, synchronous readers whenever compositing needs one. The
    /// handle and its readers hold all the state, so the source stays plain data.
    /// </remarks>
    public abstract class VideoSource : Source
    {
        /// <summary>Readies the source for one session.</summary>
        /// <param name="context">What the session needs from every source it prepares.</param>
        /// <param name="ct">Cancels preparing.</param>
        /// <returns>The prepared source; the caller disposes it.</returns>
        /// <exception cref="SourceUnavailableException">The source can't provide content.</exception>
        internal abstract Task<IPreparedVideoSource> PrepareAsync(VideoPrepareContext context, CancellationToken ct = default);

        /// <inheritdoc/>
        public override VideoSource Duplicate() => (VideoSource)base.Duplicate();

        //content time to source time through the live Start/Duration/Loop; see Source.ToSourceTime
        internal TimeSpan MapTime(TimeSpan contentTime, TimeSpan? naturalLength) => ToSourceTime(contentTime, naturalLength);

        /// <summary>One frame, for callers that need a single picture, such as thumbnails; nothing stays open afterwards.</summary>
        /// <param name="contentTime">Time since the in-point, at 1x.</param>
        /// <param name="mode">Whether to read the proxy or the original, as a session would.</param>
        /// <param name="maxWidth">The largest width wanted; 0 for the native width.</param>
        /// <param name="maxHeight">The largest height wanted; 0 for the native height.</param>
        /// <param name="ct">Cancels reading the frame.</param>
        /// <returns>The frame; the caller disposes it.</returns>
        /// <exception cref="SourceUnavailableException">The frame can't be read; with <see cref="SourceMode.ProxiesOnly"/>, a frame with no proxy is <see cref="SourceUnavailableReason.ProxyPending"/> or <see cref="SourceUnavailableReason.ProxyMissing"/>.</exception>
        public virtual Task<SKImage> GetFrameAtAsync(
            TimeSpan contentTime, SourceMode mode = SourceMode.SourceOnly, int maxWidth = 0, int maxHeight = 0,
            CancellationToken ct = default) => Task.Run(async () =>
        {
            using IPreparedVideoSource prepared = await PrepareAsync(new VideoPrepareContext(HardwareAccelerator.None, mode), ct);

            //proxies are random-access; originals are read from that point on
            VideoReadMode readMode = mode == SourceMode.ProxiesOnly ? VideoReadMode.RandomAccess : VideoReadMode.Sequential;

            using IVideoFrameReader reader = prepared.OpenReader(new VideoReaderOptions(
                readMode, contentTime, MaxWidth: maxWidth, MaxHeight: maxHeight, CallerOwnsFrames: true));

            VideoFrame frame = reader.GetFrame(contentTime);
            return frame.Transient ? frame.Image : CopyOf(frame.Image);
        }, ct);

        //a raster copy the caller can own, for a frame the reader keeps
        private protected static SKImage CopyOf(SKImage image)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Premul));

            if (!image.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes))
                throw new InvalidOperationException("Could not copy the frame's pixels.");

            return SKImage.FromBitmap(bitmap);
        }
    }
}
