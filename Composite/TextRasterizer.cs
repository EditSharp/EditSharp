using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;
using EditSharp.Components.Nodes.Sources.Video;
using EditSharp.Components;


namespace EditSharp.Composite
{
    /// <summary>
    /// Renders a TextInputNode to a transparent PNG that the pipeline then
    /// treats as an ordinary still image.
    ///
    /// TextInputNode has no font size, deliberately: a downstream
    /// TransformNode's Scale (1,1) means "as large as it can be without
    /// cropping", so the size is whatever makes the finished text block fill
    /// the canvas. That does mean editing the words changes their apparent
    /// size — a five word line and a fifty word line both fill the canvas,
    /// so the fifty word one is rendered much smaller. That is the intended
    /// behaviour of fitting to the canvas.
    ///
    /// Glyphs are drawn WHITE on transparent. Colour comes from a downstream
    /// TintNode (Animatable&lt;SKColor&gt;), which multiplies through later
    /// in the chain, so white is the identity that lets any tint work.
    ///
    /// "CLIPS ARE GRAPHS" REWRITE: takes a TextInputNode instead of the old
    /// TextClip. There is no more clip-level ClipTransform to read a max
    /// scale from for the raster-sharpness ceiling — a TextInputNode has no
    /// transform of its own (Transform now lives on whichever TransformNode
    /// is downstream, and a graph could route a TextInputNode through more
    /// than one, or none) — so ScaleCeiling's old per-clip keyframe walk is
    /// gone; this simply rasterizes at MaxRasterScale unconditionally, a
    /// documented behavioural simplification rather than an attempt to peek
    /// at a downstream node's data from here.
    /// </summary>
    internal static class TextRasterizer
    {
        /// <summary>
        /// Ceiling on how much larger than the canvas the raster may be,
        /// applied unconditionally now (see class remarks) rather than
        /// scaled down to a clip's own max keyframed scale.
        /// </summary>
        public const float MaxRasterScale = 2.0f;
 
        /// <summary>
        /// The font size used purely to measure. Layout is resolved at this size,
        /// then everything is scaled by a single factor to reach the target — text
        /// metrics are linear in size, so one measuring pass is enough.
        /// </summary>
        private const float MeasurementSize = 100f;
 
        public static string Rasterize(
            TextInputNode node, int canvasWidth, int canvasHeight,
            out int width, out int height)
        {
            List<string> lines = WrapLines(node.Content, node.WordsPerLine);
 
            if (lines.Count == 0)
                throw new InvalidOperationException("TextInputNode.Content has no renderable text.");
 
            using SKTypeface typeface = node.FontFace.ToTypeface(node.FontStyle);
            using var measuringFont = new SKFont(typeface, MeasurementSize);
 
            var (measuredWidth, measuredHeight, lineHeight, ascent) =
                MeasureBlock(lines, measuringFont);
 
            //scale the whole block so it just fits the canvas, then allow it to go
            //further only up to the raster cap
            float fit = Math.Min(
                canvasWidth / measuredWidth,
                canvasHeight / measuredHeight);
 
            float target = fit * MaxRasterScale;
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
 
                float x = node.Align switch
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
        /// A plain whitespace split. Explicit newlines in the content are
        /// honoured as hard breaks.
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
 