using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace EditSharp.Render
{
    /// <summary>
    /// Item 13: composites ONE output frame directly against an in-process
    /// SKCanvas and returns its raw RGBA8888 bytes, ready for the
    /// accumulator. This is the direct replacement for
    /// SoftwareFrameFilterChainBuilder.Build/ComposeChannel/Draw/Flatten
    /// (all deleted from FrameFilterChain.cs) — same shape (per-clip ->
    /// per-channel -> blended -> flattened onto opaque black), but every
    /// step is a real Skia draw call instead of an emitted filter-graph
    /// line, and there is no ffmpeg subprocess anywhere in this method.
    ///
    /// Per-clip compositing itself (resize -> PreTransform effects ->
    /// Modulate -> warp -> PostTransform effects) is NOT reimplemented
    /// here — that's SkiaClipCompositorSketch.Composite (items 3-6),
    /// called once per clip below. This file's own job is everything
    /// above a single clip: fetching that clip's pixel content for this
    /// frame (SkClipContentSource), handling a two-clip transition
    /// (SkTransitionCompositor), blending a channel onto the accumulator
    /// (SkChannelCompositor), and flattening onto black at the end.
    /// </summary>
    internal static class SkFrameCompositor
    {
        /// <summary>
        /// Renders one output frame and returns its raw pixel bytes in a
        /// buffer RENTED from ArrayPool&lt;byte&gt;.Shared — same
        /// LOH-avoidance discipline the old per-frame ffmpeg-pipe read
        /// used (see the old RenderFrameAsync's own remarks): a fresh
        /// ~8MB (1920x1080 rgba8888) allocation every single frame is a
        /// guaranteed Large Object Heap hit, and the LOH is only reclaimed
        /// on a full gen2 sweep. The caller MUST call
        /// ArrayPool&lt;byte&gt;.Shared.Return(buffer) once done with it,
        /// on every path including error.
        /// </summary>
        public static (byte[] Buffer, int Length) RenderFrame(
            FrameState frame, SkClipContentSource contentSource,
            int canvasWidth, int canvasHeight, int fps)
        {
            using SKSurface accumulator = SKSurface.Create(new SKImageInfo(
                canvasWidth, canvasHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
            accumulator.Canvas.Clear(SKColors.Transparent);

            foreach (FrameChannel channel in frame.Channels)
            {
                SKImage? drawn = ComposeChannel(channel, contentSource, canvasWidth, canvasHeight, fps);
                if (drawn == null) continue;

                using (drawn)
                    SkChannelCompositor.Draw(accumulator.Canvas, drawn, channel.BlendMode);
            }

            //drop the composite onto opaque black — deliberately the very
            //last step, same as the old Flatten's own placement, so every
            //blend mode and every transition above sees real transparency
            //to work with rather than a channel that was already dropped
            //onto black
            using SKSurface flattened = SKSurface.Create(new SKImageInfo(
                canvasWidth, canvasHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
            flattened.Canvas.Clear(SKColors.Black);

            using (SKImage composite = accumulator.Snapshot())
                flattened.Canvas.DrawImage(composite, 0, 0);

            return ReadRgba8888(flattened, canvasWidth, canvasHeight);
        }

        /// <summary>
        /// One channel's clips for this frame, transitioned together if a
        /// transition is mid-flight. Direct replacement for the old
        /// SoftwareFrameFilterChainBuilder.ComposeChannel — same shape,
        /// each clip drawn onto its OWN canvas-sized transparent surface
        /// first (transitions composite two whole rendered clips, not two
        /// unrendered ones) rather than straight onto the accumulator.
        /// </summary>
        private static SKImage? ComposeChannel(
            FrameChannel channel, SkClipContentSource contentSource,
            int canvasWidth, int canvasHeight, int fps)
        {
            var rendered = new List<SKImage>(channel.Clips.Count);

            foreach (FrameClip clip in channel.Clips)
            {
                using SKSurface clipSurface = SKSurface.Create(new SKImageInfo(
                    canvasWidth, canvasHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
                clipSurface.Canvas.Clear(SKColors.Transparent);

                DrawClip(clipSurface.Canvas, clip, contentSource, canvasWidth, canvasHeight, fps);

                rendered.Add(clipSurface.Snapshot());
            }

            if (rendered.Count == 0) return null;
            if (rendered.Count == 1) return rendered[0];

            using (rendered[0])
            using (rendered[1])
            {
                return SkTransitionCompositor.Compose(
                    rendered[0], rendered[1], channel.Transition, channel.TransitionProgress,
                    canvasWidth, canvasHeight);
            }
        }

        /// <summary>
        /// Fetches this clip's content for this frame and runs it through
        /// the full per-clip chain (SkiaClipCompositorSketch.Composite).
        /// modulateAlreadyApplied/preTransformEffectsBaked are both left at
        /// their default `false` — nothing bakes anything ahead of time
        /// anymore now that video decode has no pre-render step (item 11);
        /// those parameters exist on Composite for a case this pipeline no
        /// longer has.
        /// </summary>
        private static void DrawClip(
            SKCanvas canvas, FrameClip frameClip, SkClipContentSource contentSource,
            int canvasWidth, int canvasHeight, int fps)
        {
            (SKImage? content, bool transient) = contentSource.GetContent(
                frameClip, canvasWidth, canvasHeight);

            if (content == null) return; //audio-only SourceClip

            try
            {
                //GeneratorClip/NoiseClip carry NativeWidth/Height == 0 (see
                //FrameClip's own remarks) — substituted with canvas size
                //here, exactly the fallback the old ffmpeg-path BuildContent
                //applied inline for the same two clip types
                int nativeWidth = frameClip.NativeWidth > 0 ? frameClip.NativeWidth : canvasWidth;
                int nativeHeight = frameClip.NativeHeight > 0 ? frameClip.NativeHeight : canvasHeight;

                (int contentWidth, int contentHeight) = TransformExpressions.ComputeContentSize(
                    frameClip.Clip, nativeWidth, nativeHeight, canvasWidth, canvasHeight);

                var context = new SkClipChainContext(
                    canvasWidth, canvasHeight, contentWidth, contentHeight, fps, 1.0 / fps);

                SkiaClipCompositorSketch.Composite(
                    canvas, content, frameClip.Clip, frameClip.Transform,
                    nativeWidth, nativeHeight, context);
            }
            finally
            {
                if (transient) content.Dispose();
            }
        }

        /// <summary>
        /// Copies `surface`'s pixels out as tightly-packed RGBA8888 bytes
        /// (no row padding), matching exactly what ffmpeg's rawvideo
        /// demuxer expects for `-pix_fmt rgba` — see SkOutputFormat and
        /// FrameRenderer.FinalizeOutputAsync's matching input args.
        ///
        /// Pins the rented buffer via GCHandle rather than an `unsafe`
        /// block — SKImage.ReadPixels wants a raw destination pointer, and
        /// this avoids requiring AllowUnsafeBlocks on the project just for
        /// this one call site. Not build-tested in this sandbox (no
        /// SkiaSharp toolchain here) — flagged in the migration manifest's
        /// deferred-verification list alongside item 10's own
        /// not-build-tested SKRuntimeEffect API surface.
        /// </summary>
        private static (byte[] Buffer, int Length) ReadRgba8888(
            SKSurface surface, int width, int height)
        {
            int expectedBytes = width * height * SkOutputFormat.BytesPerPixel;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(expectedBytes);

            try
            {
                GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    using SKImage snapshot = surface.Snapshot();

                    var dstInfo = new SKImageInfo(
                        width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

                    bool ok = snapshot.ReadPixels(
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
    /// The pixel format the accumulator and the final ffmpeg mux/encode
    /// step both agree on. Replaces PixelFormats.Primary (gbrap16le) for
    /// the video path entirely — item 12 decided 8-bit RGBA for the whole
    /// Skia compositor, and this is where that decision actually lands as
    /// a concrete value with real code consuming it, which the manifest's
    /// item 12 write-up explicitly deferred to this item rather than
    /// guessing at ahead of time (same reasoning FrameBatchSize got).
    ///
    /// PixelFormats itself (in ClipVideoChain.cs, fully superseded) is
    /// untouched — nothing here edits or reuses it, this is a clean new
    /// constant for the new pipeline's own accumulator format, not a
    /// repurposing of the old one.
    /// </summary>
    internal static class SkOutputFormat
    {
        public const string FfmpegPixelFormat = "rgba";
        public const int BytesPerPixel = 4;
    }
}
