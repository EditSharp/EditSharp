using System.Collections.Generic;
using SkiaSharp;
using EditSharp.Components;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;
 
namespace EditSharp.Components.Nodes.Sources
{
    /// <summary>Replaces the old TextClip. No in-point concept — text has nothing to trim into.</summary>
    public sealed class TextInputNode : InputNode
    {
        string _content = null!;
        [Editable("Text", Editor = PropertyEditor.Multiline)]
        public required string Content { get => _content; set => Transaction.Set(this, ref _content, value, static (o, v) => o._content = v); }
        FontFace _fontFace = FontFace.ComicSansMs;
        [Editable("Font")]
        public FontFace FontFace { get => _fontFace; set => Transaction.Set(this, ref _fontFace, value, static (o, v) => o._fontFace = v); }
        SKFontStyle _fontStyle = SKFontStyle.Normal;
        public SKFontStyle FontStyle { get => _fontStyle; set => Transaction.Set(this, ref _fontStyle, value, static (o, v) => o._fontStyle = v); }
        SKTextAlign _align = SKTextAlign.Center;
        [Editable("Alignment")]
        public SKTextAlign Align { get => _align; set => Transaction.Set(this, ref _align, value, static (o, v) => o._align = v); }
        int _wordsPerLine = int.MaxValue;
        [Editable("Words per line", Min = 1, Max = 100, Step = 1)]
        public int WordsPerLine { get => _wordsPerLine; set => Transaction.Set(this, ref _wordsPerLine, value, static (o, v) => o._wordsPerLine = v); }
 
        private static readonly NodePort[] StaticPorts = [new("Image", PortType.Image, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override Node Duplicate() => Transaction.Suppressed(() => new TextInputNode
        {
            Enabled = Enabled,
            Content = Content,
            FontFace = FontFace,
            FontStyle = FontStyle,
            Align = Align,
            WordsPerLine = WordsPerLine,
        });
    }
}
 