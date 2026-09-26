using EditSharp.Components.Media;
using System;
using SkiaSharp;

namespace EditSharp.Components.Nodes.Input
{
    /// <summary>A generator: each frame is drawn at canvas size into a recorded picture, which the compositor rasterizes on the GPU.</summary>
    /// <remarks>Nothing is prepared, and only the node's Duration ends it.</remarks>
    internal sealed class PreparedGenerator(VideoInputNode node, Func<Time, SKSizeI, SKImage> render) : IPreparedVideoSource
    {
        public (int Width, int Height) NativeSize => (0, 0);

        public IVideoFrameReader OpenReader(VideoReaderOptions options) => new Reader(node, render, options);

        public void Dispose() { }

        private sealed class Reader(VideoInputNode node, Func<Time, SKSizeI, SKImage> render, VideoReaderOptions options) : IVideoFrameReader
        {
            public VideoFrame GetFrame(Time contentTime)
            {
                node.ToMaterialTime(contentTime, null);
                return new VideoFrame(render(contentTime, GeneratedFrames.Canvas(options)), Transient: true);
            }

            public void Dispose() { }
        }
    }

    internal static class GeneratedFrames
    {
        //with no canvas given (a one-off frame), generators draw at 1080p
        public static SKSizeI Canvas(VideoReaderOptions options) => options.CanvasWidth > 0 && options.CanvasHeight > 0
            ? new SKSizeI(options.CanvasWidth, options.CanvasHeight)
            : new SKSizeI(1920, 1080);

        public static SKImage Record(SKSizeI size, Action<SKCanvas> draw)
        {
            using var recorder = new SKPictureRecorder();
            draw(recorder.BeginRecording(new SKRect(0, 0, size.Width, size.Height)));
            using SKPicture picture = recorder.EndRecording();
            return FromPicture(picture, size);
        }

        public static SKImage FromPicture(SKPicture picture, SKSizeI size)
        {
            using (picture)
                return SKImage.FromPicture(picture, size) ?? throw new InvalidOperationException("Could not make an image from a recorded picture.");
        }
    }
}
