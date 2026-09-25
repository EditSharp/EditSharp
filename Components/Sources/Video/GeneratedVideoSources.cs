using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using EditSharp.Components;
using EditSharp.Compositing.Generators;
using EditSharp.Editing;
using EditSharp.History;

namespace EditSharp.Components.Sources.Video;

/// <summary>A solid colour filling the canvas; the colour can be keyframed.</summary>
[SourceKind("color", DisplayName = "Color")]
public class ColorVideoSource : VideoSource
{
    Animatable<SKColor> _color = new(SKColors.Black);
    /// <summary>The colour; black by default.</summary>
    [Editable("Color")]
    public Animatable<SKColor> Color { get => _color; set => Transaction.Set(this, ref _color, value, static (o, v) => o._color = v); }

    /// <inheritdoc/>
    public override IEnumerable<IAnimatable> Animatables => [Color];

    /// <inheritdoc/>
    public override ColorVideoSource Duplicate() => (ColorVideoSource)base.Duplicate();

    /// <summary>Always null: a colour has no end of its own.</summary>
    /// <param name="ct">Unused.</param>
    /// <returns>Null.</returns>
    public override Task<TimeSpan?> GetNaturalLengthAsync(CancellationToken ct = default) => Task.FromResult<TimeSpan?>(null);

    internal override Task<IPreparedVideoSource> PrepareAsync(VideoPrepareContext context, CancellationToken ct = default) =>
        Task.FromResult<IPreparedVideoSource>(new PreparedGenerator(this, (contentTime, size) =>
        {
            SKColor colour = Color.Evaluate(contentTime);
            return GeneratedFrames.Record(size, canvas => canvas.Clear(colour));
        }));
}

/// <summary>A field of gradient noise filling the canvas, changing over time.</summary>
[SourceKind("noise", DisplayName = "Noise")]
public class NoiseVideoSource : VideoSource
{
    int _seed = Random.Shared.Next();
    /// <summary>Picks the pattern; the same seed draws the same noise. Random by default.</summary>
    [Editable("Seed")]
    public int Seed { get => _seed; set => Transaction.Set(this, ref _seed, value, static (o, v) => o._seed = v); }

    float _detail = 0.03f;
    /// <summary>How fine the noise is, from 0 to 1; higher packs more cells across the canvas.</summary>
    [Editable("Detail", Min = 0, Max = 1, Step = 0.001)]
    public float Detail { get => _detail; set => Transaction.Set(this, ref _detail, value, static (o, v) => o._detail = v); }

    float _seetheRate = 0.03f;
    /// <summary>How fast the noise changes over time, from 0 (still) to 1.</summary>
    [Editable("Seethe rate", Min = 0, Max = 1, Step = 0.001)]
    public float SeetheRate { get => _seetheRate; set => Transaction.Set(this, ref _seetheRate, value, static (o, v) => o._seetheRate = v); }

    /// <inheritdoc/>
    public override NoiseVideoSource Duplicate() => (NoiseVideoSource)base.Duplicate();

    /// <summary>Always null: noise has no end of its own.</summary>
    /// <param name="ct">Unused.</param>
    /// <returns>Null.</returns>
    public override Task<TimeSpan?> GetNaturalLengthAsync(CancellationToken ct = default) => Task.FromResult<TimeSpan?>(null);

    internal override Task<IPreparedVideoSource> PrepareAsync(VideoPrepareContext context, CancellationToken ct = default) =>
        Task.FromResult<IPreparedVideoSource>(new PreparedGenerator(this, (contentTime, size) =>
            GeneratedFrames.Record(size, canvas =>
                NoiseGenerator.Draw(canvas, Seed, Detail, SeetheRate, contentTime.TotalSeconds, size.Width, size.Height))));

    internal override void AddFingerprint(ref HashCode hash)
    {
        base.AddFingerprint(ref hash);
        hash.Add(Seed);
        hash.Add(Detail);
        hash.Add(SeetheRate);
    }
}

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
[SourceKind("text", DisplayName = "Text")]
public class TextVideoSource : VideoSource, IChoiceProvider
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

    /// <inheritdoc/>
    public override TextVideoSource Duplicate() => (TextVideoSource)base.Duplicate();

    /// <summary>Always null: text has no end of its own.</summary>
    /// <param name="ct">Unused.</param>
    /// <returns>Null.</returns>
    public override Task<TimeSpan?> GetNaturalLengthAsync(CancellationToken ct = default) => Task.FromResult<TimeSpan?>(null);

    internal override Task<IPreparedVideoSource> PrepareAsync(VideoPrepareContext context, CancellationToken ct = default) =>
        Task.FromResult<IPreparedVideoSource>(new PreparedText(this));

    internal override void AddFingerprint(ref HashCode hash)
    {
        base.AddFingerprint(ref hash);
        hash.Add(Content);
        hash.Add(Font);
        hash.Add(Weight);
        hash.Add(Italic);
        hash.Add(Size);
        hash.Add(Box);
        hash.Add(Wrap);
        hash.Add(HorizontalAlignment);
        hash.Add(VerticalAlignment);
    }

    /// <summary>Text changes rarely, so each reader keeps its last recording and redoes it only when the text or canvas changes.</summary>
    private sealed class PreparedText(TextVideoSource source) : IPreparedVideoSource
    {
        public (int Width, int Height) NativeSize => (0, 0);

        public IVideoFrameReader OpenReader(VideoReaderOptions options) => new Reader(source, options);

        public void Dispose() { }

        private sealed class Reader(TextVideoSource source, VideoReaderOptions options) : IVideoFrameReader
        {
            private object? _key;
            private SKImage? _image;

            public VideoFrame GetFrame(TimeSpan contentTime)
            {
                source.MapTime(contentTime, null);
                SKSizeI canvas = GeneratedFrames.Canvas(options);

                object key = (source.Content, source.Font, source.Weight, source.Italic,
                    source.Size, source.Box, source.Wrap, source.HorizontalAlignment, source.VerticalAlignment, canvas);

                if (_image is null || !key.Equals(_key))
                {
                    _image?.Dispose();
                    _key = key;

                    var recorded = TextRasterizer.Record(
                        source.Content, source.Font, source.Weight, source.Italic, source.Size, source.Box, source.Wrap,
                        source.HorizontalAlignment, source.VerticalAlignment, canvas.Width, canvas.Height);

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

/// <summary>A generator: each frame is drawn at canvas size into a recorded picture, which the compositor rasterizes on the GPU.</summary>
/// <remarks>Nothing is prepared, and only the source's Duration ends it.</remarks>
internal sealed class PreparedGenerator(VideoSource source, Func<TimeSpan, SKSizeI, SKImage> render) : IPreparedVideoSource
{
    public (int Width, int Height) NativeSize => (0, 0);

    public IVideoFrameReader OpenReader(VideoReaderOptions options) => new Reader(source, render, options);

    public void Dispose() { }

    private sealed class Reader(VideoSource source, Func<TimeSpan, SKSizeI, SKImage> render, VideoReaderOptions options) : IVideoFrameReader
    {
        public VideoFrame GetFrame(TimeSpan contentTime)
        {
            source.MapTime(contentTime, null);
            return new VideoFrame(render(contentTime, GeneratedFrames.Canvas(options)), Transient: true);
        }

        public void Dispose() { }
    }
}

internal static class GeneratedFrames
{
    //with no canvas given (a one-off frame), generators draw at 1080p
    public static SKSizeI Canvas(VideoReaderOptions options) => options.CanvasWidth > 0 && options.CanvasHeight > 0
        ? new SKSizeI(options.CanvasWidth, options.CanvasHeight)
        : new SKSizeI(1920, 1080);

    public static SKImage Record(SKSizeI size, Action<SKCanvas> draw)
    {
        using var recorder = new SKPictureRecorder();
        draw(recorder.BeginRecording(new SKRect(0, 0, size.Width, size.Height)));
        using SKPicture picture = recorder.EndRecording();
        return FromPicture(picture, size);
    }

    public static SKImage FromPicture(SKPicture picture, SKSizeI size)
    {
        using (picture)
            return SKImage.FromPicture(picture, size) ?? throw new InvalidOperationException("Could not make an image from a recorded picture.");
    }
}
