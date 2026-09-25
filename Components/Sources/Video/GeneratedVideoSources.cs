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
    [Editable("Color")]
    public Animatable<SKColor> Color { get => _color; set => Transaction.Set(this, ref _color, value, static (o, v) => o._color = v); }

    public override IEnumerable<IAnimatable> Animatables => [Color];

    public override ColorVideoSource Duplicate() => (ColorVideoSource)base.Duplicate();

    public override Task<TimeSpan?> GetNaturalLengthAsync(CancellationToken ct = default) => Task.FromResult<TimeSpan?>(null);

    internal override Task<IPreparedVideoSource> PrepareAsync(VideoPrepareContext context, CancellationToken ct = default) =>
        Task.FromResult<IPreparedVideoSource>(new PreparedGenerator(this, (contentTime, size) =>
        {
            SKColor colour = Color.Evaluate(contentTime);
            return GeneratedFrames.Record(size, canvas => canvas.Clear(colour));
        }));
}

/// <summary>An evolving noise field filling the canvas.</summary>
[SourceKind("noise", DisplayName = "Noise")]
public class NoiseVideoSource : VideoSource
{
    int _seed = Random.Shared.Next();
    [Editable("Seed")]
    public int Seed { get => _seed; set => Transaction.Set(this, ref _seed, value, static (o, v) => o._seed = v); }

    float _detail = 0.03f;
    [Editable("Detail", Min = 0, Max = 1, Step = 0.001)]
    public float Detail { get => _detail; set => Transaction.Set(this, ref _detail, value, static (o, v) => o._detail = v); }

    float _seetheRate = 0.03f;
    [Editable("Seethe rate", Min = 0, Max = 1, Step = 0.001)]
    public float SeetheRate { get => _seetheRate; set => Transaction.Set(this, ref _seetheRate, value, static (o, v) => o._seetheRate = v); }

    public override NoiseVideoSource Duplicate() => (NoiseVideoSource)base.Duplicate();

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

public enum TextWrap { Off, WrapWords, WrapCharacters }

/// <summary>Justify stretches every wrapped line to the box's width; a paragraph's last line stays left.</summary>
public enum HorizontalTextAlignment { Left, Center, Right, Justify }

public enum VerticalTextAlignment { Top, Center, Bottom }

/// <summary>
/// A block of white text at a fixed size, centred in a frame-sized image, so
/// a transform places it and editing the words never resizes them.
/// </summary>
[SourceKind("text", DisplayName = "Text")]
public class TextVideoSource : VideoSource, IChoiceProvider
{
    string _content = "Text";
    [Editable("Text", Editor = PropertyEditor.Multiline)]
    public string Content { get => _content; set => Transaction.Set(this, ref _content, value, static (o, v) => o._content = v); }

    //an installed family's name; one that isn't installed draws with the default font
    //changing it moves Weight to the nearest weight the new font has
    string _font = "Arial";
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

    //100 (thin) to 900 (black); the dropdown offers the weights the font has
    int _weight = 400;
    [Editable("Weight")]
    public int Weight { get => _weight; set => Transaction.Set(this, ref _weight, value, static (o, v) => o._weight = v); }

    //the font's italic, or the upright slanted when it has none
    bool _italic;
    [Editable("Italic")]
    public bool Italic { get => _italic; set => Transaction.Set(this, ref _italic, value, static (o, v) => o._italic = v); }

    //the font's em size, a fraction of the frame width
    float _size = 0.05f;
    [Editable("Size", Min = 0.001, Max = 1, Step = 0.001, Frame = FrameMeasure.Width)]
    public float Size { get => _size; set => Transaction.Set(this, ref _size, value, static (o, v) => o._size = v); }

    //the area the text is laid out in, centred in the frame: x of the frame width, y of its height
    Vector2 _box = new(0.9f, 0.9f);
    [Editable("Box", Min = 0.01, Max = 1, Step = 0.01, Frame = FrameMeasure.Frame)]
    public Vector2 Box { get => _box; set => Transaction.Set(this, ref _box, value, static (o, v) => o._box = v); }

    //where lines break when they reach the box's width
    TextWrap _wrap = TextWrap.WrapWords;
    [Editable("Wrap")]
    public TextWrap Wrap { get => _wrap; set => Transaction.Set(this, ref _wrap, value, static (o, v) => o._wrap = v); }

    HorizontalTextAlignment _horizontalAlignment = HorizontalTextAlignment.Center;
    [Editable("Horizontal alignment")]
    public HorizontalTextAlignment HorizontalAlignment { get => _horizontalAlignment; set => Transaction.Set(this, ref _horizontalAlignment, value, static (o, v) => o._horizontalAlignment = v); }

    VerticalTextAlignment _verticalAlignment = VerticalTextAlignment.Center;
    [Editable("Vertical alignment")]
    public VerticalTextAlignment VerticalAlignment { get => _verticalAlignment; set => Transaction.Set(this, ref _verticalAlignment, value, static (o, v) => o._verticalAlignment = v); }

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

    public override TextVideoSource Duplicate() => (TextVideoSource)base.Duplicate();

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

/// <summary>
/// A generator: each frame is drawn at the canvas size into a recorded
/// picture, which the compositor rasterizes on the GPU when it draws it.
/// Nothing is prepared; the only thing that ends it is its Duration.
/// </summary>
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
