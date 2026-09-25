using EditSharp.Components.Nodes;
using EditSharp.Components.Nodes.Input;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using SkiaSharp;
using EditSharp.Components;

namespace EditSharp.Compositing.Generators
{
    /// <summary>
    /// Lays text out as white glyphs on a transparent, frame-sized picture:
    /// a fixed font size, inside a box centred in the frame. Colour comes
    /// from a downstream TintNode; placement from a TransformNode.
    /// </summary>
    internal static class TextRasterizer
    {
        //the picture is drawn this much larger than the frame so scaling the clip up stays sharp
        public const float MaxRasterScale = 2.0f;

        /// <summary>One line to draw, and whether it ends its paragraph (Justify leaves those alone).</summary>
        internal readonly record struct Line(string Text, bool EndsParagraph);

        /// <summary>
        /// Records `content` at `size` (a fraction of the frame width) inside
        /// `box` (fractions of the frame's width and height, centred), broken
        /// at newlines and as `wrap` says at the box's width. Text past the
        /// box still draws; only the frame cuts it. Null when there's nothing
        /// to draw.
        /// </summary>
        public static (SKPicture Picture, int Width, int Height)? Record(
            string content, string family, int weight, bool italic, float size, Vector2 box, TextWrap wrap,
            HorizontalTextAlignment horizontal, VerticalTextAlignment vertical, int canvasWidth, int canvasHeight)
        {
            if (string.IsNullOrWhiteSpace(content) || size <= 0) return null;

            var style = new SKFontStyle(weight, (int)SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
            using SKTypeface typeface = FontFamilies.Resolve(family, style);
            using var font = new SKFont(typeface, size * canvasWidth);

            //no italic face: slant the upright
            if (italic && !typeface.IsItalic) font.SkewX = -0.25f;

            float boxWidth = Math.Max(1f, box.X * canvasWidth);
            float boxHeight = Math.Max(1f, box.Y * canvasHeight);
            float boxLeft = (canvasWidth - boxWidth) / 2f;
            float boxTop = (canvasHeight - boxHeight) / 2f;

            List<Line> lines = Lines(content, font, boxWidth, wrap);

            SKFontMetrics metrics = font.Metrics;
            float lineHeight = metrics.Descent - metrics.Ascent + metrics.Leading;
            float blockHeight = lineHeight * lines.Count;

            float top = boxTop + vertical switch
            {
                VerticalTextAlignment.Top => 0,
                VerticalTextAlignment.Bottom => boxHeight - blockHeight,
                _ => (boxHeight - blockHeight) / 2f,
            };

            int width = (int)Math.Ceiling(canvasWidth * MaxRasterScale);
            int height = (int)Math.Ceiling(canvasHeight * MaxRasterScale);

            using var recorder = new SKPictureRecorder();
            SKCanvas canvas = recorder.BeginRecording(new SKRect(0, 0, width, height));
            canvas.Scale(MaxRasterScale);
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };

            for (int i = 0; i < lines.Count; i++)
            {
                //DrawText places the baseline; the ascent is negative
                float baseline = top + i * lineHeight - metrics.Ascent;
                Line line = lines[i];

                if (horizontal == HorizontalTextAlignment.Justify && wrap != TextWrap.Off && !line.EndsParagraph
                    && DrawJustified(canvas, line.Text, boxLeft, boxWidth, baseline, font, paint))
                    continue;

                float lineWidth = font.MeasureText(line.Text);
                float x = boxLeft + horizontal switch
                {
                    HorizontalTextAlignment.Center => (boxWidth - lineWidth) / 2f,
                    HorizontalTextAlignment.Right => boxWidth - lineWidth,
                    _ => 0,
                };

                canvas.DrawText(line.Text, x, baseline, SKTextAlign.Left, font, paint);
            }

            return (recorder.EndRecording(), width, height);
        }

        //spreads the space left over between the line's words; false for a single word, which stays left
        private static bool DrawJustified(SKCanvas canvas, string text, float left, float width, float baseline, SKFont font, SKPaint paint)
        {
            string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2) return false;

            float[] widths = [.. words.Select(w => font.MeasureText(w))];
            float gap = (width - widths.Sum()) / (words.Length - 1);

            float x = left;
            for (int i = 0; i < words.Length; i++)
            {
                canvas.DrawText(words[i], x, baseline, SKTextAlign.Left, font, paint);
                x += widths[i] + gap;
            }

            return true;
        }

        /// <summary>
        /// The lines to draw: one per newline, each broken to fit `boxWidth`
        /// between words or between characters. With words, a word wider than
        /// the box gets a line to itself.
        /// </summary>
        public static List<Line> Lines(string content, SKFont font, float boxWidth, TextWrap wrap)
        {
            var lines = new List<Line>();

            foreach (string paragraph in content.ReplaceLineEndings("\n").Split('\n'))
            {
                List<string> broken = wrap switch
                {
                    TextWrap.WrapWords => ByWords(paragraph, font, boxWidth),
                    TextWrap.WrapCharacters => ByCharacters(paragraph, font, boxWidth),
                    _ => [paragraph],
                };

                for (int i = 0; i < broken.Count; i++) lines.Add(new Line(broken[i], i == broken.Count - 1));
            }

            return lines;
        }

        private static List<string> ByWords(string paragraph, SKFont font, float boxWidth)
        {
            var lines = new List<string>();
            string line = "";

            foreach (string word in paragraph.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
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
            return lines;
        }

        //breaks wherever the box is full, keeping each character whole (a surrogate pair or combining mark with its base)
        private static List<string> ByCharacters(string paragraph, SKFont font, float boxWidth)
        {
            var lines = new List<string>();
            string line = "";
            TextElementEnumerator characters = StringInfo.GetTextElementEnumerator(paragraph);

            while (characters.MoveNext())
            {
                string character = characters.GetTextElement();

                //a line doesn't start with the space it broke at
                if (line.Length == 0 && lines.Count > 0 && string.IsNullOrWhiteSpace(character)) continue;

                string longer = line + character;

                if (line.Length > 0 && font.MeasureText(longer) > boxWidth)
                {
                    lines.Add(line.TrimEnd());
                    line = string.IsNullOrWhiteSpace(character) ? "" : character;
                }
                else line = longer;
            }

            lines.Add(line);
            return lines;
        }
    }
}
