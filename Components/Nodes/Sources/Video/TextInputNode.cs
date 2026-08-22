using System.Collections.Generic;
using SkiaSharp;
using EditSharp.Components;
using EditSharp.Components.Nodes;
 
namespace EditSharp.Components.Nodes.Sources.Video
{
    /// <summary>Replaces the old TextClip. No in-point concept — text has nothing to trim into.</summary>
    public sealed class TextInputNode : InputNode
    {
        public required string Content { get; set; }
        public FontFace FontFace { get; set; } = FontFace.ComicSansMs;
        public SKFontStyle FontStyle { get; set; } = SKFontStyle.Normal;
        public SKTextAlign Align { get; set; } = SKTextAlign.Center;
        public int WordsPerLine { get; set; } = int.MaxValue;
 
        private static readonly NodePort[] StaticPorts = [new("Image", PortType.Image, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override Node Duplicate() => new TextInputNode
        {
            Enabled = Enabled,
            Content = Content,
            FontFace = FontFace,
            FontStyle = FontStyle,
            Align = Align,
            WordsPerLine = WordsPerLine,
        };
    }
}
 