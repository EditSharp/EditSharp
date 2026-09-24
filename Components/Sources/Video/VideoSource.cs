using System;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using EditSharp.Video;

namespace EditSharp.Components.Sources.Video
{
    /// <summary>
    /// A source of frames. Reading is two-phase: PrepareAsync does the slow,
    /// once-per-session work (probing, choosing which file/decoder to use) and
    /// hands back a prepared handle; the handle then opens cheap, synchronous
    /// readers whenever compositing needs one. The handle and its readers hold
    /// all state; the source itself stays plain data.
    /// </summary>
    public abstract class VideoSource : Source
    {
        internal abstract Task<IPreparedVideoSource> PrepareAsync(VideoPrepareContext context, CancellationToken ct = default);

        public override VideoSource Duplicate() => (VideoSource)base.Duplicate();

        /// <summary>
        /// One frame at `contentTime`, for callers that just need a single
        /// picture (thumbnails); nothing stays open afterwards. The caller owns
        /// the result. `mode` picks proxy or original like a session would
        /// (ProxiesOnly throws ProxyPending where the proxy doesn't reach yet);
        /// `maxWidth`/`maxHeight` cap the size (0 = native). Kinds with a
        /// cheaper route (a still image) override it.
        /// </summary>
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
