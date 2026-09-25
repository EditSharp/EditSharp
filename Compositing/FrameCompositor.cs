using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SkiaSharp;
using EditSharp.Components.Clips;
using EditSharp.Compositing.Gpu;
using EditSharp.Compositing.Sources;

namespace EditSharp.Compositing
{
    /// <summary>Composites one frame: each channel's clips (through ClipCompositor), transitions, channel blending, then onto black.</summary>
    /// <remarks>It works the same whichever <see cref="Sources.IClipContentSource"/> supplies the clips' content.</remarks>
    internal static class FrameCompositor
    {
        //one frame as RGBA8888 bytes in a buffer rented from ArrayPool<byte>.Shared; the caller returns it
        public static (byte[] Buffer, int Length) RenderFrame(
            FrameState frame, IClipContentSource contentSource,
            int canvasWidth, int canvasHeight, int fps, SurfacePool pool)
        {
            using SKImage flattened = ComposeFrameImage(frame, contentSource, canvasWidth, canvasHeight, fps, pool);
            return ReadRgba8888(flattened, canvasWidth, canvasHeight);
        }

        //one frame as an opaque, frame-sized image the caller disposes; nested timelines use it directly
        public static SKImage ComposeFrameImage(
            FrameState frame, IClipContentSource contentSource,
            int canvasWidth, int canvasHeight, int fps, SurfacePool pool)
        {
            SKSurface accumulator = pool.Rent(canvasWidth, canvasHeight);
            SKImage composite;
            try
            {
                accumulator.Canvas.Clear(SKColors.Transparent);

                foreach (FrameChannel channel in frame.Channels)
                {
                    SKImage? drawn = ComposeChannel(channel, frame.FrameIndex, contentSource, canvasWidth, canvasHeight, fps, pool);
                    if (drawn == null) continue;

                    using (drawn)
                        ChannelCompositor.Draw(accumulator.Canvas, drawn, channel.BlendMode);
                }

                composite = accumulator.Snapshot();
            }
            finally
            {
                pool.Return(accumulator, canvasWidth, canvasHeight);
            }

            //onto black last, so every blend mode and transition above works with real transparency
            SKSurface flattened = pool.Rent(canvasWidth, canvasHeight);
            try
            {
                flattened.Canvas.Clear(SKColors.Black);

                using (composite)
                    flattened.Canvas.DrawImage(composite, 0, 0);

                return flattened.Snapshot();
            }
            finally
            {
                pool.Return(flattened, canvasWidth, canvasHeight);
            }
        }

        //one channel's clips this frame; each is drawn onto its own transparent surface first, since a
        //transition combines two finished clips
        private static SKImage? ComposeChannel(
            FrameChannel channel, int frameIndex, IClipContentSource contentSource,
            int canvasWidth, int canvasHeight, int fps, SurfacePool pool)
        {
            var rendered = new List<SKImage>(channel.Clips.Count);

            foreach (FrameClip clip in channel.Clips)
            {
                SKSurface clipSurface = pool.Rent(canvasWidth, canvasHeight);
                try
                {
                    clipSurface.Canvas.Clear(SKColors.Transparent);

                    DrawClip(clipSurface.Canvas, clip, frameIndex, contentSource, canvasWidth, canvasHeight, fps, pool);

                    rendered.Add(clipSurface.Snapshot());
                }
                finally
                {
                    pool.Return(clipSurface, canvasWidth, canvasHeight);
                }
            }

            if (rendered.Count == 0) return null;
            if (rendered.Count == 1) return rendered[0];

            using (rendered[0])
            using (rendered[1])
            {
                return TransitionCompositor.Compose(
                    rendered[0], rendered[1], channel.Transition, channel.TransitionProgress,
                    canvasWidth, canvasHeight, pool);
            }
        }

        //gets the clip's content for this frame, evaluates its graph through ClipCompositor, then
        //disposes whatever content was handed over as transient
        private static void DrawClip(
            SKCanvas canvas, FrameClip frameClip, int frameIndex, IClipContentSource contentSource,
            int canvasWidth, int canvasHeight, int fps, SurfacePool pool)
        {
            if (frameClip.Clip is not VideoClip clip) return; //only video channels are resolved into frames

            IReadOnlyDictionary<Guid, (SKImage Image, bool Transient)> resolved =
                contentSource.GetContent(clip, frameClip.Graph, frameClip.ClipSeconds, frameIndex, canvasWidth, canvasHeight, pool);

            var plain = new Dictionary<Guid, SKImage>(resolved.Count);
            foreach (KeyValuePair<Guid, (SKImage Image, bool Transient)> entry in resolved)
                plain[entry.Key] = entry.Value.Image;

            try
            {
                var context = new SkClipChainContext(canvasWidth, canvasHeight, fps);

                ClipCompositor.Composite(
                    canvas, frameClip.Graph, plain, frameClip.ClipSeconds, context, pool);
            }
            finally
            {
                foreach (KeyValuePair<Guid, (SKImage Image, bool Transient)> entry in resolved)
                {
                    if (entry.Value.Transient) entry.Value.Image.Dispose();
                }
            }
        }

        //the surface's pixels as tightly packed RGBA8888, which ffmpeg's rawvideo input expects for rgba
        private static (byte[] Buffer, int Length) ReadRgba8888(
            SKImage image, int width, int height)
        {
            int expectedBytes = width * height * OutputFormat.BytesPerPixel;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(expectedBytes);

            try
            {
                GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    var dstInfo = new SKImageInfo(
                        width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

                    bool ok = image.ReadPixels(
                        dstInfo, handle.AddrOfPinnedObject(), width * OutputFormat.BytesPerPixel);

                    if (!ok)
                        throw new InvalidOperationException(
                            "Failed to read the composited frame's pixel data.");
                }
                finally
                {
                    handle.Free();
                }
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }

            return (buffer, expectedBytes);
        }
    }
}