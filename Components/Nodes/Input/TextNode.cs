using EditSharp.Components.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using EditSharp.Compositing.Generators;
using EditSharp.Editing;
using EditSharp.History;

namespace EditSharp.Components.Nodes.Input
{
    /// <summary>Where text breaks onto a new line when it reaches the width of its box.</summary>
    public enum TextWrap
    {
        /// <summary>Lines break only at line breaks in the text.</summary>
        Off,

        /// <summary>Lines break between words; a word wider than the box breaks between characters.</summary>
        WrapWords,

        /// <summary>Lines break between any two characters.</summary>
        WrapCharacters,
    }

    /// <summary>How lines of text line up across their box.</summary>
    public enum HorizontalTextAlignment
    {
        /// <summary>Lines start at the box's left edge.</summary>
        Left,

        /// <summary>Lines are centred in the box.</summary>
        Center,

        /// <summary>Lines end at the box's right edge.</summary>
        Right,

        /// <summary>Wrapped lines stretch to the box's full width; a paragraph's last line stays left.</summary>
        Justify,
    }

    /// <summary>Where a block of text sits in its box, top to bottom.</summary>
    public enum VerticalTextAlignment
    {
        /// <summary>The text starts at the top of the box.</summary>
        Top,

        /// <summary>The text is centred in the box.</summary>
        Center,

        /// <summary>The text ends at the bottom of the box.</summary>
        Bottom,
    }

    /// <summary>A block of white text on a transparent, frame-sized image.</summary>
    /// <remarks>The text is sized against the frame, not fitted to its box, so editing the words never resizes them; a transform places it.</remarks>
    [NodeKind("text", DisplayName = "Text")]
    public sealed class TextNode : VideoInputNode, IChoiceProvider
    {
        string _content = "Text";
        /// <summary>The text; line breaks start new paragraphs.</summary>
        [Editable("Text", Editor = PropertyEditor.Multiline)]
        public string Content { get => _content; set => Transaction.Set(this, ref _content, value, static (o, v) => o._content = v); }

        string _font = "Arial";
        /// <summary>The name of an installed font family; one that isn't installed draws with the default font.</summary>
        /// <remarks>Changing it moves <see cref="Weight"/> to the nearest weight the new font has.</remarks>
        [Editable("Font")]
        public string Font
        {
            get => _font;
            set
            {
                Transaction.Set(this, ref _font, value, static (o, v) => o._font = v);
                int nearest = FontFamilies.Nearest(FontFamilies.WeightsOf(value), Weight);
                if (nearest != Weight) Weight = nearest;
            }
        }

        int _weight = 400;
        /// <summary>How heavy the letters are, from 100 (thin) to 900 (black); 400 is regular.</summary>
        [Editable("Weight")]
        public int Weight { get => _weight; set => Transaction.Set(this, ref _weight, value, static (o, v) => o._weight = v); }

        bool _italic;
        /// <summary>Whether to use the font's italic; a font with none is slanted instead.</summary>
        [Editable("Italic")]
        public bool Italic { get => _italic; set => Transaction.Set(this, ref _italic, value, static (o, v) => o._italic = v); }

        float _size = 0.05f;
        /// <summary>The font's em size, as a fraction of the frame's width.</summary>
        [Editable("Size", Min = 0.001, Max = 1, Step = 0.001, Frame = FrameMeasure.Width)]
        public float Size { get => _size; set => Transaction.Set(this, ref _size, value, static (o, v) => o._size = v); }

        Vector2 _box = new(0.9f, 0.9f);
        /// <summary>The size of the area the text is laid out in, centred in the frame: X as a fraction of the frame's width, Y of its height.</summary>
        [Editable("Box", Min = 0.01, Max = 1, Step = 0.01, Frame = FrameMeasure.Frame)]
        public Vector2 Box { get => _box; set => Transaction.Set(this, ref _box, value, static (o, v) => o._box = v); }

        TextWrap _wrap = TextWrap.WrapWords;
        /// <summary>Where lines break when they reach the box's width.</summary>
        [Editable("Wrap")]
        public TextWrap Wrap { get => _wrap; set => Transaction.Set(this, ref _wrap, value, static (o, v) => o._wrap = v); }

        HorizontalTextAlignment _horizontalAlignment = HorizontalTextAlignment.Center;
        /// <summary>How lines line up across the box.</summary>
        [Editable("Horizontal alignment")]
        public HorizontalTextAlignment HorizontalAlignment { get => _horizontalAlignment; set => Transaction.Set(this, ref _horizontalAlignment, value, static (o, v) => o._horizontalAlignment = v); }

        VerticalTextAlignment _verticalAlignment = VerticalTextAlignment.Center;
        /// <summary>Where the text sits in the box, top to bottom.</summary>
        [Editable("Vertical alignment")]
        public VerticalTextAlignment VerticalAlignment { get => _verticalAlignment; set => Transaction.Set(this, ref _verticalAlignment, value, static (o, v) => o._verticalAlignment = v); }

        /// <summary>The choices for <see cref="Font"/> (the installed families) and <see cref="Weight"/> (the weights the font has).</summary>
        /// <remarks>A font that isn't installed, or a weight the font doesn't have, is offered too, so the current value always shows.</remarks>
        /// <param name="property">The property's name.</param>
        /// <returns>The choices; null for any other property.</returns>
        public IReadOnlyList<Choice>? ChoicesFor(string property)
        {
            if (property == nameof(Weight))
            {
                //a missing font offers the standard weights
                IReadOnlyList<int> weights = FontFamilies.WeightsOf(Font) is { Count: > 0 } has ? has : [100, 200, 300, 400, 500, 600, 700, 800, 900];
                List<Choice> named = [.. weights.Select(w => new Choice(w, FontFamilies.WeightName(w)))];
                if (!weights.Contains(Weight)) named.Insert(0, new Choice(Weight, $"{FontFamilies.WeightName(Weight)} ({Weight})"));
                return named;
            }

            if (property != nameof(Font)) return null;

            List<Choice> choices = [.. FontFamilies.Installed.Select(f => new Choice(f, f))];
            if (!choices.Any(c => string.Equals((string)c.Value, Font, StringComparison.OrdinalIgnoreCase)))
                choices.Insert(0, new Choice(Font, $"{Font} (missing)"));

            return choices;
        }

        /// <summary>Always null: text has no end of its own.</summary>
        /// <param name="ct">Unused.</param>
        /// <returns>Null.</returns>
        public override Task<TimeSpan?> GetNaturalLengthAsync(CancellationToken ct = default) => Task.FromResult<TimeSpan?>(null);

        internal override Task<IPreparedVideoSource> PrepareAsync(VideoPrepareContext context, CancellationToken ct = default) =>
            Task.FromResult<IPreparedVideoSource>(new PreparedText(this));

        /// <summary>Text changes rarely, so each reader keeps its last recording and redoes it only when the text or canvas changes.</summary>
        private sealed class PreparedText(TextNode node) : IPreparedVideoSource
        {
            public (int Width, int Height) NativeSize => (0, 0);

            public IVideoFrameReader OpenReader(VideoReaderOptions options) => new Reader(node, options);

            public void Dispose() { }

            private sealed class Reader(TextNode node, VideoReaderOptions options) : IVideoFrameReader
            {
                private object? _key;
                private SKImage? _image;

                public VideoFrame GetFrame(TimeSpan contentTime)
                {
                    node.ToMaterialTime(contentTime, null);
                    SKSizeI canvas = GeneratedFrames.Canvas(options);

                    object key = (node.Content, node.Font, node.Weight, node.Italic,
                        node.Size, node.Box, node.Wrap, node.HorizontalAlignment, node.VerticalAlignment, canvas);

                    if (_image is null || !key.Equals(_key))
                    {
                        _image?.Dispose();
                        _key = key;

                        var recorded = TextRasterizer.Record(
                            node.Content, node.Font, node.Weight, node.Italic, node.Size, node.Box, node.Wrap,
                            node.HorizontalAlignment, node.VerticalAlignment, canvas.Width, canvas.Height);

                        //nothing to write draws nothing
                        _image = recorded is { } r
                            ? GeneratedFrames.FromPicture(r.Picture, new SKSizeI(r.Width, r.Height))
                            : GeneratedFrames.Record(new SKSizeI(1, 1), _ => { });
                    }

                    return new VideoFrame(_image, Transient: false);
                }

                public void Dispose() => _image?.Dispose();
            }
        }
    }
}
