using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SkiaSharp;
using EditSharp;
using EditSharp.Components.Effects;

namespace EditSharp.Render
{
    /// <summary>
    /// The individual effects, for both pipeline stages.
    ///
    /// Every effect here operates on a canvas-sized RGBA stream and returns
    /// another one. What differs between the stages is what that stream contains:
    /// pre-transform it is the content sitting upright in the middle of the frame,
    /// post-transform it is the content already positioned, scaled and warped.
    ///
    /// Anything that resamples or blurs splits colour from alpha and recombines
    /// with alphamerge, for the same reason the transform does — operating on
    /// straight RGBA drags edge colour toward the transparent surround and leaves
    /// a dark fringe that only becomes visible once something later rewrites alpha.
    /// </summary>
    internal static class ClipEffects
    {
        //keyed on the mask's only real degrees of freedom: its buffer size and
        //radius. Every clip with a RoundedCornersEffect at a given size/radius
        //shares one file instead of each frame rasterizing its own — see
        //GetOrCreateMask
        private static readonly ConcurrentDictionary<(int Width, int Height, float Radius), string> MaskCache = new();

        /// <summary>
        /// Runs every enabled effect assigned to the given stage, in list order.
        /// </summary>
        public static string ApplyStage(
            List<Effect> effects, EffectStage stage, string label, ClipChainContext context)
        {
            if (effects == null || effects.Count == 0) return label;

            string current = label;

            foreach (Effect effect in effects.Where(e => e.Enabled && e.Stage == stage))
            {
                current = effect switch
                {
                    RoundedCornersEffect rounded => ApplyRoundedCorners(rounded, current, stage, context),
                    DropShadowEffect shadow => ApplyDropShadow(shadow, current, context),
                    BlurEffect blur => ApplyBlur(blur, current, context),
                    _ => throw new NotSupportedException($"Unknown Effect subtype: {effect.GetType().Name}"),
                };
            }

            return current;
        }

        /// <summary>
        /// Rounds the content's corners by multiplying a rounded-rectangle mask
        /// into the stream's existing alpha.
        ///
        /// Multiplying rather than replacing matters: content can arrive with
        /// transparency of its own (a logo, the gaps between glyphs), and an
        /// alphamerge would throw that away and hand back a solid rounded
        /// rectangle. blend=all_mode=multiply is used for the combine — this
        /// project has hit unreliable blend behaviour before, so it was verified
        /// directly on grayscale streams rather than trusted.
        ///
        /// Pre-transform the mask covers the content's own rectangle inside the
        /// frame, so the rounding rotates and warps along with the content.
        /// Post-transform the content is already warped and no longer axis-aligned,
        /// so a rectangular mask no longer matches its shape — see the note in
        /// RasterizeRoundedRectMask.
        /// </summary>
        private static string ApplyRoundedCorners(
            RoundedCornersEffect effect, string label, EffectStage stage, ClipChainContext context)
        {
            var placement = context.Placement;

            //pre-transform the mask covers exactly the content's own rectangle
            //inside the frame; post-transform the content has been warped across
            //the whole canvas, so the mask has to cover all of it
            int maskWidth = stage == EffectStage.PreTransform ? placement.Width : context.FrameWidth;
            int maskHeight = stage == EffectStage.PreTransform ? placement.Height : context.FrameHeight;
            int maskX = stage == EffectStage.PreTransform ? placement.X : 0;
            int maskY = stage == EffectStage.PreTransform ? placement.Y : 0;

            string maskPath = GetOrCreateMask(maskWidth, maskHeight, effect.Radius, context.TempFiles);

            //raw gray16le, not a PNG — the file already contains exactly the
            //pixel format the rest of the chain works in, so there's no
            //decode or format conversion to do on the way in
            int index = context.Graph.AddInput(maskPath, true,
            [
                "-f", "rawvideo",
                "-pix_fmt", PixelFormats.Gray,
                "-s", $"{maskWidth}x{maskHeight}",
                "-r", context.Fps.ToString(CultureInfo.InvariantCulture),
            ]);

            //when baking a WHOLE clip's worth of frames in one continuous
            //re-encode (RepeatStaticInputs), the single raw mask frame needs
            //to persist across every output frame, not just the first. That
            //has to happen as a FILTER (the `loop` filter, looping 1 frame
            //starting at frame 0, indefinitely) rather than an input option —
            //`-loop` is specific to the image2 demuxer and rawvideo doesn't
            //support it at all ("Option loop not found", confirmed directly).
            //The ordinary per-frame path renders exactly one output frame per
            //ffmpeg process, so the single raw frame is read once there and
            //never needs looping either way
            string maskSource = $"{index}:v";
            if (context.RepeatStaticInputs)
            {
                string looped = context.Graph.NextLabel("efroundmaskloop");
                context.Graph.FilterLines.Add($"[{maskSource}]loop=loop=-1:size=1:start=0[{looped}]");
                maskSource = looped;
            }

            string maskLabel = context.Graph.NextLabel("efroundmask");
            context.Graph.FilterLines.Add(
                $"[{maskSource}]pad={context.FrameWidth}:{context.FrameHeight}:{maskX}:{maskY}:color=black[{maskLabel}]");

            string colourCopy = context.Graph.NextLabel("efroundcol");
            string alphaCopy = context.Graph.NextLabel("efroundalpha");
            context.Graph.FilterLines.Add($"[{label}]split=2[{colourCopy}][{alphaCopy}]");

            string existingAlpha = context.Graph.NextLabel("efroundsrcalpha");
            context.Graph.FilterLines.Add($"[{alphaCopy}]format={PixelFormats.Primary},alphaextract[{existingAlpha}]");

            string combined = context.Graph.NextLabel("efroundcomb");
            context.Graph.FilterLines.Add(
                //shortest=1 is required, not cosmetic, now that the mask can
                //be an infinitely-looped stream (RepeatStaticInputs — see
                //above): blend's own default is shortest=0, meaning it runs
                //until the LONGER of its two inputs ends rather than the
                //shorter one. With an infinite mask as the second input,
                //"longer" never arrives, and the whole graph — everything
                //downstream of this blend, all the way to the encoder — never
                //terminates either, growing the output file without bound.
                //Confirmed directly: this is exactly what a real baked render
                //did (grew to 55GB before being killed) before shortest was
                //added. Harmless in the ordinary single-frame per-frame path,
                //where both inputs are already exactly one frame regardless.
                $"[{existingAlpha}][{maskLabel}]blend=all_mode=multiply:shortest=1[{combined}]");

            string next = context.Graph.NextLabel("efround");
            context.Graph.FilterLines.Add($"[{colourCopy}][{combined}]alphamerge[{next}]");

            return next;
        }

        /// <summary>
        /// Casts a shadow shaped like the content's own alpha.
        ///
        /// The silhouette comes from alphaextract on the live stream rather than
        /// being rebuilt from geometry, so it follows whatever shape the content
        /// actually has at this point in the chain — including its own
        /// transparency, and including any rounding an earlier effect applied.
        /// That makes effect ORDER meaningful: rounded corners then drop shadow
        /// casts a rounded shadow, the reverse casts a square one.
        /// </summary>
        private static string ApplyDropShadow(
            DropShadowEffect effect, string label, ClipChainContext context)
        {
            string colourCopy = context.Graph.NextLabel("efshadowsrc");
            string silhouetteCopy = context.Graph.NextLabel("efshadowsil");
            context.Graph.FilterLines.Add($"[{label}]split=2[{colourCopy}][{silhouetteCopy}]");

            string silhouette = context.Graph.NextLabel("efshadowmask");
            context.Graph.FilterLines.Add(
                $"[{silhouetteCopy}]format={PixelFormats.Primary},alphaextract," +
                $"lut=y='val*{GraphUtilities.Num(Math.Clamp(effect.Opacity, 0f, 1f))}'[{silhouette}]");

            string colourHex =
                $"{effect.Color.Red:x2}{effect.Color.Green:x2}{effect.Color.Blue:x2}";

            double sigma = ShadowSigma(effect, context);

            //Offset is normalized the same way Position is — a fraction of half the
            //canvas — and Y is up, so a positive Y offset lifts the shadow
            int offsetX = (int)Math.Round(effect.Offset.X * context.CanvasWidth / 2.0);
            int offsetY = (int)Math.Round(-effect.Offset.Y * context.CanvasHeight / 2.0);

            //gblur can only spread into pixels that actually exist in its input
            //frame. Building the shadow at exactly canvas size means content
            //sitting near an edge — which happens constantly on an animated clip
            //moving through the frame — gets its blur hard-truncated at that edge
            //instead of tapering to zero, and the truncation becomes visible the
            //moment the (then-shifted) shadow is composited: a flat cut instead of
            //a soft edge. Padding on all four sides before blurring gives the
            //taper somewhere real to go regardless of where the clip currently
            //sits. 3 sigma covers effectively all of a Gaussian's energy; the
            //offset shift is folded into the same margin since the crop below
            //recovers it for free alongside the padding.
            //sigma and the offsets above are normalized against the CANVAS and stay
            //that way — a shadow must not change size or direction just because its
            //clip got a smaller working frame. Only the BUFFER follows the frame.
            int margin = (int)Math.Ceiling(sigma * 3) + Math.Max(Math.Abs(offsetX), Math.Abs(offsetY));
            int paddedWidth = context.FrameWidth + margin * 2;
            int paddedHeight = context.FrameHeight + margin * 2;

            //The margin is built by overlaying the silhouette onto an explicitly
            //ZEROED gray base rather than by `pad`. `pad` converts its colour
            //through limited range, so on a gray plane "black" fills with 16, not
            //0 — the same trap documented in GraphUtilities.BuildTransparentBlank,
            //measured again here directly (pad fill = 16, base+overlay fill = 0).
            //A 16/255 floor across the padding is roughly 6% opacity, which gblur
            //then smears through the whole shadow buffer: a faint rectangular haze
            //around every drop shadow, and one whose extent depends on the buffer
            //size. setrange and scale=out_range=full were both tried and neither
            //changes what pad writes.
            string padBase = context.Graph.NextLabel("efshadowpadbase");
            context.Graph.FilterLines.Add(
                $"color=black:size={paddedWidth}x{paddedHeight}:rate={context.Fps}:" +
                $"duration={GraphUtilities.Num(context.DurationSeconds)}," +
                $"format={PixelFormats.Gray},lut=y=0[{padBase}]");

            string silhouettePadded = context.Graph.NextLabel("efshadowmaskpad");
            context.Graph.FilterLines.Add(
                $"[{padBase}][{silhouette}]overlay={margin}:{margin}[{silhouettePadded}]");

            string shadowColour = context.Graph.NextLabel("efshadowcolor");
            context.Graph.FilterLines.Add(
                $"color=0x{colourHex}:size={paddedWidth}x{paddedHeight}:" +
                $"rate={context.Fps}:duration={GraphUtilities.Num(context.DurationSeconds)}," +
                $"format={PixelFormats.Primary}[{shadowColour}]");

            string shadowShape = context.Graph.NextLabel("efshadowshape");
            context.Graph.FilterLines.Add(
                $"[{shadowColour}][{silhouettePadded}]alphamerge," +
                $"gblur=sigma={GraphUtilities.Num(sigma)}[{shadowShape}]");

            //Cropping back down to canvas size recovers the padding AND the offset
            //shift in one step: sampling the padded buffer starting at
            //(margin - offsetX, margin - offsetY) instead of (margin, margin) is
            //exactly equivalent to the old overlay-at-offset step, except the data
            //now sampled near the edges is real blur falloff rather than the
            //zeros a canvas-sized buffer would have clipped it to. margin is sized
            //so this crop origin always stays within the padded buffer.
            int cropX = margin - offsetX;
            int cropY = margin - offsetY;

            string positioned = context.Graph.NextLabel("efshadowpos");
            context.Graph.FilterLines.Add(
                $"[{shadowShape}]crop={context.FrameWidth}:{context.FrameHeight}:{cropX}:{cropY}[{positioned}]");

            string next = context.Graph.NextLabel("efshadow");
            context.Graph.FilterLines.Add(
                $"[{positioned}][{colourCopy}]overlay=0:0:format=auto[{next}]");

            return next;
        }

        /// <summary>
        /// BlurRadius is normalized against canvas width, same as BlurEffect.Radius
        /// and DropShadowEffect.Offset, so a shadow keeps its proportions when the
        /// same timeline is rendered at a different output resolution.
        /// </summary>
        //gblur's sigma is only valid in [0, 1024] — clamped here rather than left
        //to blow up ffmpeg, since BlurRadius is user-settable and a big enough
        //value (or a very wide canvas) can push the raw product past that no
        //matter what the default is
        private static double ShadowSigma(DropShadowEffect effect, ClipChainContext context) =>
            Math.Clamp(effect.BlurRadius * context.CanvasWidth, 0.1, 1024.0);

        /// <summary>
        /// Gaussian blur, colour and alpha blurred separately.
        ///
        /// Blurring straight RGBA in one pass pulls edge colour toward the
        /// transparent surround, so the softened edge comes out muddied. Splitting
        /// keeps the colour blur working on real content — the border is smeared
        /// outward first — while the alpha blur alone decides the softened shape.
        /// </summary>
        private static string ApplyBlur(
            BlurEffect effect, string label, ClipChainContext context)
        {
            //Radius is normalized against canvas width so a blur looks the same at
            //any output resolution. Clamped to gblur's actual valid range
            //[0, 1024] — same reasoning as ShadowSigma above.
            double sigma = Math.Clamp(effect.Radius * context.CanvasWidth, 0.1, 1024.0);

            //smear out to the content's own edge, not a fixed narrow frame border —
            //see the same reasoning in ClipVideoChain.ApplyTransform
            var placement = context.Placement;
            int left = placement.X;
            int right = context.FrameWidth - placement.X - placement.Width;
            int top = placement.Y;
            int bottom = context.FrameHeight - placement.Y - placement.Height;

            string colourCopy = context.Graph.NextLabel("efblurcol");
            string alphaCopy = context.Graph.NextLabel("efbluralpha");
            context.Graph.FilterLines.Add($"[{label}]split=2[{colourCopy}][{alphaCopy}]");

            string blurredAlpha = context.Graph.NextLabel("efblurmask");
            context.Graph.FilterLines.Add(
                $"[{alphaCopy}]format={PixelFormats.Primary},alphaextract," +
                $"gblur=sigma={GraphUtilities.Num(sigma)}[{blurredAlpha}]");

            string blurredColour = context.Graph.NextLabel("efblurrgb");
            context.Graph.FilterLines.Add(
                $"[{colourCopy}]format={PixelFormats.Gbrp}," +
                $"fillborders=left={left}:right={right}:top={top}:bottom={bottom}:mode=smear," +
                $"gblur=sigma={GraphUtilities.Num(sigma)},format={PixelFormats.Gbrap}[{blurredColour}]");

            string next = context.Graph.NextLabel("efblur");
            context.Graph.FilterLines.Add($"[{blurredColour}][{blurredAlpha}]alphamerge[{next}]");

            return next;
        }

        /// <summary>
        /// Returns the mask for (width, height, radius), rasterizing it the
        /// first time that combination is seen and reusing the file on every
        /// subsequent call. The mask is a pure function of these three values,
        /// and none of them vary frame to frame for a given clip — without this
        /// cache the identical mask was being rebuilt (Skia draw + disk write)
        /// from scratch on every single frame the clip is visible on.
        ///
        /// GetOrAdd's factory isn't guaranteed atomic across threads, so two
        /// concurrent frame renders can race and both rasterize before either
        /// publishes — harmless (both are byte-identical), just occasionally
        /// wasted work. The loser deletes its own copy rather than leaking a
        /// file nothing will ever reference; only the winner registers its path
        /// with tempFiles, so cleanup doesn't accumulate duplicate entries
        /// across every frame that happens to look this key up.
        /// </summary>
        private static string GetOrCreateMask(
            int width, int height, float radiusFraction, ConcurrentBag<string> tempFiles)
        {
            var key = (width, height, radiusFraction);

            if (MaskCache.TryGetValue(key, out string? existing)) return existing;

            string candidate = RasterizeRoundedRectMask(width, height, radiusFraction);

            if (MaskCache.TryAdd(key, candidate))
            {
                tempFiles.Add(candidate);
                return candidate;
            }

            try { File.Delete(candidate); } catch { /* best-effort */ }
            return MaskCache[key];
        }

        /// <summary>
        /// Rasterizes a white rounded rectangle on black as RAW gray16le pixel
        /// data — not a PNG. This mask is fed straight into the per-frame filter
        /// graph as optimized media (see OptimizedMediaBuilder's own reasoning
        /// for choosing FFV1 over a compressed codec): there's no reason to pay
        /// PNG encode cost writing it and decode+format-conversion cost reading
        /// it back when the rest of the pipeline already standardizes on raw
        /// 16-bit planes. Skia antialiases the corner arcs, which is the whole
        /// reason this isn't built from ffmpeg primitives instead.
        ///
        /// Radius is a 0-1 fraction where 1 rounds each corner as far as it can
        /// go — half the shorter side, giving a pill or a circle.
        ///
        /// Note this always produces an AXIS-ALIGNED rounded rectangle. That is
        /// correct pre-transform, where the content is upright and the mask warps
        /// along with it afterwards. Assigning this effect to PostTransform on a
        /// rotated or warped clip would round the corners of the frame rather than
        /// of the content.
        /// </summary>
        public static string RasterizeRoundedRectMask(int width, int height, float radiusFraction)
        {
            float radius = Math.Clamp(radiusFraction, 0f, 1f) * (Math.Min(width, height) / 2f);

            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);

            SKCanvas canvas = surface.Canvas;
            canvas.Clear(SKColors.Black);

            using var paint = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
            };

            canvas.DrawRoundRect(new SKRect(0, 0, width, height), radius, radius, paint);
            canvas.Flush();

            string path = GraphUtilities.GetImageTempFilePath(
                $"roundmask_{Guid.NewGuid():N}.raw");

            using SKImage image = surface.Snapshot();
            using SKPixmap pixmap = image.PeekPixels();

            WriteGray16LeFromRed(pixmap, width, height, path);

            return path;
        }

        /// <summary>
        /// Takes the red channel (== green == blue here, since the draw above is
        /// pure black/white) and expands each 8-bit sample to 16-bit LE by
        /// replicating the byte into both octets (v, v) — 255 maps to 65535
        /// exactly this way, matching the value*257 mapping ffmpeg's own
        /// 8-to-16-bit conversions use, rather than the value*256 a naive
        /// left-shift would produce.
        /// </summary>
        private static void WriteGray16LeFromRed(SKPixmap pixmap, int width, int height, string path)
        {
            ReadOnlySpan<byte> pixels = pixmap.GetPixelSpan();
            int stride = pixmap.RowBytes;

            using FileStream stream = File.OpenWrite(path);
            byte[] row = new byte[width * 2];

            for (int y = 0; y < height; y++)
            {
                int rowStart = y * stride;
                for (int x = 0; x < width; x++)
                {
                    byte v = pixels[rowStart + (x * 4)]; // R of RGBA8888
                    row[x * 2] = v;
                    row[(x * 2) + 1] = v;
                }
                stream.Write(row, 0, row.Length);
            }
        }
    }
}
