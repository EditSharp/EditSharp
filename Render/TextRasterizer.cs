using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;
using EditSharp.Components;
using EditSharp.Components.Clips;

namespace EditSharp.Render
{
    /// <summary>
    /// Renders a TextClip to a transparent PNG that the pipeline then treats as an
    /// ordinary still image.
    ///
    /// TextClip has no font size, deliberately: Scale (1,1) means "as large as it
    /// can be without cropping", so the size is whatever makes the finished text
    /// block fill the canvas. That does mean editing the words changes their
    /// apparent size — a five word line and a fifty word line both fill the canvas,
    /// so the fifty word one is rendered much smaller. That is the intended
    /// behaviour of fitting to the canvas.
    ///
    /// Glyphs are drawn WHITE on transparent. Colour comes from Clip.Modulate,
    /// which multiplies through later in the chain, so white is the identity that
    /// lets any tint work.
    /// </summary>
    internal static class TextRasterizer
    {
        /// <summary>
        /// Ceiling on how much larger than the canvas the raster may be. Keyframed
        /// scale can zoom well past 1, and rasterizing at that full size keeps the
        /// text sharp while zoomed — but an animation scaling to 8 would otherwise
        /// rasterize at eight times canvas width, which is enormous for no visible
        /// benefit. Past this cap the text simply softens as it is scaled up.
        /// </summary>
        public const float MaxRasterScale = 2.0f;

        /// <summary>
        /// The font size used purely to measure. Layout is resolved at this size,
        /// then everything is scaled by a single factor to reach the target — text
        /// metrics are linear in size, so one measuring pass is enough.
        /// </summary>
        private const float MeasurementSize = 100f;

        public static string Rasterize(
            TextClip clip, int canvasWidth, int canvasHeight,
            out int width, out int height)
        {
            List<string> lines = WrapLines(clip.Content, clip.WordsPerLine);

            if (lines.Count == 0)
                throw new InvalidOperationException("TextClip.Content has no renderable text.");

            using SKTypeface typeface = clip.FontFace.ToTypeface(clip.FontStyle);
            using var measuringFont = new SKFont(typeface, MeasurementSize);

            var (measuredWidth, measuredHeight, lineHeight, ascent) =
                MeasureBlock(lines, measuringFont);

            //scale the whole block so it just fits the canvas, then allow it to go
            //further only up to the raster cap
            float fit = Math.Min(
                canvasWidth / measuredWidth,
                canvasHeight / measuredHeight);

            float target = fit * ScaleCeiling(clip);
            float fontSize = MeasurementSize * target;

            using var font = new SKFont(typeface, fontSize);
            var (blockWidth, blockHeight, scaledLineHeight, scaledAscent) =
                MeasureBlock(lines, font);

            width = Math.Max(2, (int)Math.Ceiling(blockWidth));
            height = Math.Max(2, (int)Math.Ceiling(blockHeight));

            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using SKSurface surface = SKSurface.Create(info);
            SKCanvas canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            using var paint = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
            };

            for (int i = 0; i < lines.Count; i++)
            {
                float lineWidth = font.MeasureText(lines[i]);

                float x = clip.Align switch
                {
                    SKTextAlign.Left => 0,
                    SKTextAlign.Right => width - lineWidth,
                    _ => (width - lineWidth) / 2f,
                };

                //DrawText positions by baseline, so step down by the ascent to get
                //the line's top edge where it belongs
                float baseline = (i * scaledLineHeight) - scaledAscent;

                canvas.DrawText(lines[i], x, baseline, SKTextAlign.Left, font, paint);
            }

            canvas.Flush();

            string path = GraphUtilities.GetImageTempFilePath($"text_{Guid.NewGuid():N}.png");

            using (SKImage image = surface.Snapshot())
            using (SKData data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (FileStream stream = File.OpenWrite(path))
            {
                data.SaveTo(stream);
            }

            return path;
        }

        /// <summary>
        /// Splits the content into lines of at most WordsPerLine words.
        ///
        /// A plain whitespace split, NOT FuzzyMatcher.SplitKeywords — that helper
        /// lowercases everything it returns, which is correct for fuzzy matching
        /// and quite wrong for text that is about to be drawn on screen.
        /// Explicit newlines in the content are honoured as hard breaks.
        /// </summary>
        public static List<string> WrapLines(string content, int wordsPerLine)
        {
            var lines = new List<string>();
            int perLine = wordsPerLine <= 0 ? int.MaxValue : wordsPerLine;

            foreach (string hardLine in content.Split('\n'))
            {
                string[] words = hardLine.Split(
                    (char[]?)null, StringSplitOptions.RemoveEmptyEntries);

                if (words.Length == 0) continue;

                for (int i = 0; i < words.Length; i += perLine)
                {
                    lines.Add(string.Join(' ', words.Skip(i).Take(perLine)));
                }
            }

            return lines;
        }

        /// <summary>
        /// The largest scale the clip ever reaches, so the raster stays sharp
        /// through a zoom rather than being sized for the opening frame.
        /// </summary>
        private static float ScaleCeiling(TextClip clip)
        {
            float largest = Math.Max(clip.Transform.Scale.X, clip.Transform.Scale.Y);

            foreach (Keyframe keyframe in clip.Keyframes)
            {
                largest = Math.Max(largest,
                    Math.Max(keyframe.Transform.Scale.X, keyframe.Transform.Scale.Y));
            }

            return Math.Clamp(largest, 0.01f, MaxRasterScale);
        }

        private static (float Width, float Height, float LineHeight, float Ascent) MeasureBlock(
            List<string> lines, SKFont font)
        {
            SKFontMetrics metrics = font.Metrics;

            float lineHeight = metrics.Descent - metrics.Ascent + metrics.Leading;
            //explicit lambda, not a method group: SKFont.MeasureText has several
            //overloads, so `lines.Max(font.MeasureText)` cannot resolve to
            //Func<string, float> and the compiler falls through to
            //Max(IEnumerable<T>, IComparer<T>) instead
            float widest = lines.Max(line => font.MeasureText(line));

            return (Math.Max(widest, 1f), Math.Max(lineHeight * lines.Count, 1f),
                    lineHeight, metrics.Ascent);
        }
    }
}
