using System;
using System.Collections.Generic;
using SkiaSharp;
using EditSharp.Components.Sources;

namespace EditSharp.Compositing.Sources
{
    /// <summary>
    /// What compositing shows in place of a source that couldn't provide a
    /// frame, chosen by the reason it gave: a labeled "no signal" card for
    /// every reason except EndOfSource, which is simply transparent; the
    /// source has nothing more to contribute, so the node contributes nothing.
    ///
    /// Cached per (size, reason) and shared process-wide: callers must treat
    /// these as non-transient and never dispose them.
    /// </summary>
    internal static class MediaPlaceholder
    {
        private static readonly Dictionary<(int Width, int Height, SourceUnavailableReason Reason), SKImage> Cache = new();
        private static readonly object Lock = new();

        public static string LabelFor(SourceUnavailableReason reason) => reason switch
        {
            SourceUnavailableReason.Opening => "LOADING…",
            SourceUnavailableReason.ProxyPending => "GENERATING PROXY…",
            SourceUnavailableReason.ProxyMissing => "PROXY NOT GENERATED",
            SourceUnavailableReason.MediaOffline => "MEDIA OFFLINE",
            SourceUnavailableReason.DecodeError => "CAN'T DECODE",
            SourceUnavailableReason.EndOfSource => "",
            _ => "UNAVAILABLE",
        };

        public static SKImage Get(int width, int height, SourceUnavailableReason reason)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);

            lock (Lock)
            {
                if (Cache.TryGetValue((width, height, reason), out SKImage? cached))
                    return cached;

                SKImage image = reason == SourceUnavailableReason.EndOfSource
                    ? RenderTransparent(width, height)
                    : Render(width, height, LabelFor(reason));

                Cache[(width, height, reason)] = image;
                return image;
            }
        }

        private static SKImage RenderTransparent(int width, int height)
        {
            using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            bitmap.Erase(SKColors.Transparent);
            return SKImage.FromBitmap(bitmap);
        }

        private static SKImage Render(int width, int height, string label)
        {
            using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(new SKColor(28, 28, 32));

                //diagonal hazard stripes read as "no signal", even at thumbnail size
                int shortSide = Math.Min(width, height);
                int stripeSpacing = Math.Max(6, shortSide / 10);
                using var stripePaint = new SKPaint { Color = new SKColor(20, 20, 24), IsAntialias = false };

                for (int x = -height; x < width; x += stripeSpacing)
                {
                    using var path = new SKPath();
                    float half = stripeSpacing / 2f;
                    path.MoveTo(x, 0);
                    path.LineTo(x + height, height);
                    path.LineTo(x + height + half, height);
                    path.LineTo(x + half, 0);
                    path.Close();
                    canvas.DrawPath(path, stripePaint);
                }

                //too small to read? the stripes alone are the signal
                if (shortSide >= 32)
                {
                    using SKTypeface typeface = SKFontManager.Default.MatchFamily(null, SKFontStyle.Normal) ?? SKTypeface.CreateDefault();

                    //shrink long labels to fit the width
                    float fontSize = Math.Max(10, shortSide / 8f);
                    using var font = new SKFont(typeface, fontSize);
                    float measured = font.MeasureText(label);
                    if (measured > width * 0.9f) font.Size = Math.Max(8, fontSize * width * 0.9f / measured);

                    using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };

                    //DrawText positions by baseline; centre the ascent-descent box instead
                    SKFontMetrics metrics = font.Metrics;
                    float baseline = (height / 2f) - (metrics.Ascent + metrics.Descent) / 2f;

                    canvas.DrawText(label, width / 2f, baseline, SKTextAlign.Center, font, textPaint);
                }

                canvas.Flush();
            }

            return SKImage.FromBitmap(bitmap);
        }
    }
}
