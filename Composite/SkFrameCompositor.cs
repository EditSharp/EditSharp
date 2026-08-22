using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SkiaSharp;
using EditSharp.Components.Clips;
 
namespace EditSharp.Composite
{
    /// <summary>
    /// Composites ONE output frame directly against an in-process SKCanvas.
    /// Per-clip compositing itself (resolve inputs -> graph evaluation ->
    /// warp) is NOT reimplemented here — that's SkiaClipCompositorSketch.Composite
    /// (-> EffectGraphEvaluatorSk), called once per clip below. This file's own
    /// job is everything above a single clip: fetching that clip's pixel
    /// content for this frame (SkClipContentSource), handling a two-clip
    /// transition (SkTransitionCompositor), blending a channel onto the
    /// accumulator (SkChannelCompositor), and flattening onto black at the end.
    ///
    /// "CLIPS ARE GRAPHS" REWRITE:
    ///   - FrameClip no longer carries Transform/NativeWidth/NativeHeight (see
    ///     FrameFilterChain.cs's own remarks) — DrawClip fetches a dictionary
    ///     of per-InputNode content from SkClipContentSource keyed by node Id
    ///     and hands the WHOLE dictionary to SkiaClipCompositorSketch.Composite,
    ///     rather than a single pre-resolved image.
    ///   - The single top-level RenderFrame (raw RGBA8888 bytes, for the
    ///     render accumulator) is now built on top of a new, more general
    ///     ComposeFrameImage (an SKImage, flattened onto opaque black) — the
    ///     same building block NestedTimelineRenderer uses to embed one
    ///     Timeline's rendered picture as another clip's TimelineVideoInputNode
    ///     content, recursively.
    /// </summary>
    internal static class SkFrameCompositor
    {
        /// <summary>
        /// Renders one output frame and returns its raw pixel bytes in a
        /// buffer RENTED from ArrayPool&lt;byte&gt;.Shared. The caller MUST
        /// call ArrayPool&lt;byte&gt;.Shared.Return(buffer) once done with
        /// it, on every path including error.
        /// </summary>
        public static (byte[] Buffer, int Length) RenderFrame(
            FrameState frame, SkClipContentSource contentSource,
            int canvasWidth, int canvasHeight, int fps, SkSurfacePool pool)
        {
            using SKImage flattened = ComposeFrameImage(frame, contentSource, canvasWidth, canvasHeight, fps, pool);
            return ReadRgba8888(flattened, canvasWidth, canvasHeight);
        }
 
        /// <summary>
        /// Renders one output frame and returns it as an opaque (flattened
        /// onto black), canvas-sized SKImage — the OWNER is the caller, who
        /// must dispose it. Shared by RenderFrame (which reads it back to raw
        /// bytes for the accumulator) and NestedTimelineRenderer (which
        /// embeds it directly as another clip's resolved InputNode content,
        /// with no intermediate byte round-trip).
        /// </summary>
        public static SKImage ComposeFrameImage(
            FrameState frame, SkClipContentSource contentSource,
            int canvasWidth, int canvasHeight, int fps, SkSurfacePool pool)
        {
            SKSurface accumulator = pool.Rent(canvasWidth, canvasHeight);
            SKImage composite;
            try
            {
                accumulator.Canvas.Clear(SKColors.Transparent);
 
                foreach (FrameChannel channel in frame.Channels)
                {
                    SKImage? drawn = ComposeChannel(channel, contentSource, canvasWidth, canvasHeight, fps, pool);
                    if (drawn == null) continue;
 
                    using (drawn)
                        SkChannelCompositor.Draw(accumulator.Canvas, drawn, channel.BlendMode);
                }
 
                composite = accumulator.Snapshot();
            }
            finally
            {
                pool.Return(accumulator, canvasWidth, canvasHeight);
            }
 
            //drop the composite onto opaque black — deliberately the very
            //last step, so every blend mode and every transition above sees
            //real transparency to work with rather than a channel that was
            //already dropped onto black
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
 
        /// <summary>
        /// One channel's clips for this frame, transitioned together if a
        /// transition is mid-flight. Each clip is drawn onto its OWN
        /// canvas-sized transparent surface first (transitions composite two
        /// whole rendered clips, not two unrendered ones) rather than straight
        /// onto the accumulator.
        /// </summary>
        private static SKImage? ComposeChannel(
            FrameChannel channel, SkClipContentSource contentSource,
            int canvasWidth, int canvasHeight, int fps, SkSurfacePool pool)
        {
            var rendered = new List<SKImage>(channel.Clips.Count);
 
            foreach (FrameClip clip in channel.Clips)
            {
                SKSurface clipSurface = pool.Rent(canvasWidth, canvasHeight);
                try
                {
                    clipSurface.Canvas.Clear(SKColors.Transparent);
 
                    DrawClip(clipSurface.Canvas, clip, contentSource, canvasWidth, canvasHeight, fps, pool);
 
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
                return SkTransitionCompositor.Compose(
                    rendered[0], rendered[1], channel.Transition, channel.TransitionProgress,
                    canvasWidth, canvasHeight, pool);
            }
        }
 
        /// <summary>
        /// Fetches this clip's content for this frame — one resolved image
        /// per InputNode in its graph (SkClipContentSource.GetContent) — and
        /// hands the whole graph off to SkiaClipCompositorSketch.Composite for
        /// evaluation, then disposes whichever of those resolved images were
        /// transient (see SkClipContentSource's own Transient contract).
        ///
        /// FrameClip.Clip is guaranteed to be a VideoClip — only VideoChannels
        /// (and therefore only VideoClip) are ever resolved into a
        /// FrameChannel — see FrameStateResolver.
        /// </summary>
        private static void DrawClip(
            SKCanvas canvas, FrameClip frameClip, SkClipContentSource contentSource,
            int canvasWidth, int canvasHeight, int fps, SkSurfacePool pool)
        {
            if (frameClip.Clip is not VideoClip clip) return; //defensive — see class remarks
 
            IReadOnlyDictionary<Guid, (SKImage Image, bool Transient)> resolved =
                contentSource.GetContent(clip, frameClip.ClipSeconds, canvasWidth, canvasHeight, pool);
 
            var plain = new Dictionary<Guid, SKImage>(resolved.Count);
            foreach (KeyValuePair<Guid, (SKImage Image, bool Transient)> entry in resolved)
                plain[entry.Key] = entry.Value.Image;
 
            try
            {
                var context = new SkClipChainContext(canvasWidth, canvasHeight, fps, 1.0 / fps);
 
                SkiaClipCompositorSketch.Composite(
                    canvas, clip, plain, frameClip.ClipSeconds, context, pool);
            }
            finally
            {
                foreach (KeyValuePair<Guid, (SKImage Image, bool Transient)> entry in resolved)
                {
                    if (entry.Value.Transient) entry.Value.Image.Dispose();
                }
            }
        }
 
        /// <summary>
        /// Copies `surface`'s pixels out as tightly-packed RGBA8888 bytes (no
        /// row padding), matching exactly what ffmpeg's rawvideo demuxer
        /// expects for `-pix_fmt rgba` — see SkOutputFormat and
        /// Renderer.FinalizeOutputAsync's matching input args.
        /// </summary>
        private static (byte[] Buffer, int Length) ReadRgba8888(
            SKImage image, int width, int height)
        {
            int expectedBytes = width * height * SkOutputFormat.BytesPerPixel;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(expectedBytes);
 
            try
            {
                GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    var dstInfo = new SKImageInfo(
                        width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
 
                    bool ok = image.ReadPixels(
                        dstInfo, handle.AddrOfPinnedObject(), width * SkOutputFormat.BytesPerPixel);
 
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
 
    /// <summary>
    /// The pixel format the accumulator and the final ffmpeg mux/encode step
    /// both agree on. 8-bit RGBA for the whole Skia compositor.
    /// </summary>
    internal static class SkOutputFormat
    {
        public const string FfmpegPixelFormat = "rgba";
        public const int BytesPerPixel = 4;
    }
}
 