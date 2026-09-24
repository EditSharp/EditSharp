using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using EditSharp.Components;

namespace EditSharp.Compositing.Generators
{
    /// <summary>
    /// Lays text out as white glyphs on a transparent, frame-sized picture:
    /// a fixed font size, lines wrapped by width, the block centred in the
    /// frame. Colour comes from a downstream TintNode; placement from a
    /// TransformNode.
    /// </summary>
    internal static class TextRasterizer
    {
        //the picture is drawn this much larger than the frame so scaling the clip up stays sharp
        public const float MaxRasterScale = 2.0f;

        /// <summary>
        /// Records `content` at `size` (a fraction of the frame width), lines
        /// broken at newlines and, when `wrap`, wherever a line would pass
        /// `wrapWidth` (also of the frame width). Lines align within the block.
        /// Null when there's nothing to draw.
        /// </summary>
        public static (SKPicture Picture, int Width, int Height)? Record(
            string content, string family, int weight, bool italic, SKTextAlign align, float size, bool wrap, float wrapWidth,
            int canvasWidth, int canvasHeight)
        {
            if (string.IsNullOrWhiteSpace(content) || size <= 0) return null;

            var style = new SKFontStyle(weight, (int)SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
            using SKTypeface typeface = FontFamilies.Resolve(family, style);
            using var font = new SKFont(typeface, size * canvasWidth);

            //no italic face: slant the upright
            if (italic && !typeface.IsItalic) font.SkewX = -0.25f;

            float boxWidth = wrap ? Math.Max(1f, wrapWidth * canvasWidth) : float.PositiveInfinity;
            List<string> lines = Lines(content, font, boxWidth);

            SKFontMetrics metrics = font.Metrics;
            float lineHeight = metrics.Descent - metrics.Ascent + metrics.Leading;
            float blockWidth = wrap ? boxWidth : lines.Max(line => font.MeasureText(line));
            float blockHeight = lineHeight * lines.Count;

            float left = (canvasWidth - blockWidth) / 2f;
            float top = (canvasHeight - blockHeight) / 2f;

            int width = (int)Math.Ceiling(canvasWidth * MaxRasterScale);
            int height = (int)Math.Ceiling(canvasHeight * MaxRasterScale);

            using var recorder = new SKPictureRecorder();
            SKCanvas canvas = recorder.BeginRecording(new SKRect(0, 0, width, height));
            canvas.Scale(MaxRasterScale);
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };

            for (int i = 0; i < lines.Count; i++)
            {
                float lineWidth = font.MeasureText(lines[i]);
                float x = left + align switch
                {
                    SKTextAlign.Left => 0,
                    SKTextAlign.Right => blockWidth - lineWidth,
                    _ => (blockWidth - lineWidth) / 2f,
                };

                //DrawText places the baseline; the ascent is negative
                canvas.DrawText(lines[i], x, top + i * lineHeight - metrics.Ascent, SKTextAlign.Left, font, paint);
            }

            return (recorder.EndRecording(), width, height);
        }

        /// <summary>The lines to draw: one per newline, each broken between words to fit `boxWidth`. A word wider than the box gets a line to itself.</summary>
        public static List<string> Lines(string content, SKFont font, float boxWidth)
        {
            var lines = new List<string>();

            foreach (string hardLine in content.ReplaceLineEndings("\n").Split('\n'))
            {
                string[] words = hardLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                string line = "";

                foreach (string word in words)
                {
                    string longer = line.Length == 0 ? word : line + " " + word;

                    if (line.Length > 0 && font.MeasureText(longer) > boxWidth)
                    {
                        lines.Add(line);
                        line = word;
                    }
                    else line = longer;
                }

                lines.Add(line);
            }

            return lines;
        }
    }
}
