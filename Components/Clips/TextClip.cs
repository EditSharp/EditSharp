using EditSharp.Components.Clips;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EditSharp.Components
{
    public class TextClip : Clip
    {
        public required string Content { get; set; }

        public FontFace FontFace { get; set; } = FontFace.ComicSansMs;

        public SKFontStyle FontStyle { get; set; } = SKFontStyle.Normal;

        public SKTextAlign Align { get; set; } = SKTextAlign.Center;

        public int WordsPerLine { get; set; } = int.MaxValue;

        public override TextClip Duplicate()
        {
            return new()
            {
                Content = Content,
                FontFace = FontFace,
                FontStyle = FontStyle,
                Start = Start,
                Duration = Duration,
                Modulate = Modulate,
                Transform = Transform.Duplicate(),
                Keyframes = [.. Keyframes.Select(k => k.Duplicate())],
                Effects = [.. Effects.Select(e => e.Duplicate())],
            };
        }
    }
}
