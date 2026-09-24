using System;
using System.Collections.Generic;
using System.Linq;
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
    string _font = "Arial";
    [Editable("Font")]
    public string Font { get => _font; set => Transaction.Set(this, ref _font, value, static (o, v) => o._font = v); }

    SKFontStyle _fontStyle = SKFontStyle.Normal;
    public SKFontStyle FontStyle { get => _fontStyle; set => Transaction.Set(this, ref _fontStyle, value, static (o, v) => o._fontStyle = v); }

    //the font's em size, a fraction of the frame width
    float _size = 0.05f;
    [Editable("Size", Min = 0.001, Max = 1, Step = 0.001, Frame = FrameMeasure.Width)]
    public float Size { get => _size; set => Transaction.Set(this, ref _size, value, static (o, v) => o._size = v); }

    SKTextAlign _align = SKTextAlign.Center;
    [Editable("Alignment")]
    public SKTextAlign Align { get => _align; set => Transaction.Set(this, ref _align, value, static (o, v) => o._align = v); }

    bool _wrap = true;
    [Editable("Wrap")]
    public bool Wrap { get => _wrap; set => Transaction.Set(this, ref _wrap, value, static (o, v) => o._wrap = v); }

    //the widest a line gets before it wraps, a fraction of the frame width
    float _wrapWidth = 0.9f;
    [Editable("Wrap width", Min = 0.01, Max = 1, Step = 0.01, Frame = FrameMeasure.Width)]
    [VisibleWhen(nameof(Wrap), true)]
    public float WrapWidth { get => _wrapWidth; set => Transaction.Set(this, ref _wrapWidth, value, static (o, v) => o._wrapWidth = v); }

    public IReadOnlyList<Choice>? ChoicesFor(string property)
    {
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
        hash.Add(FontStyle.Weight);
        hash.Add(FontStyle.Width);
        hash.Add(FontStyle.Slant);
        hash.Add(Align);
        hash.Add(Size);
        hash.Add(Wrap);
        hash.Add(WrapWidth);
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

                object key = (source.Content, source.Font, source.FontStyle.Weight, source.FontStyle.Width, source.FontStyle.Slant,
                    source.Align, source.Size, source.Wrap, source.WrapWidth, canvas);

                if (_image is null || !key.Equals(_key))
                {
                    _image?.Dispose();
                    _key = key;

                    var recorded = TextRasterizer.Record(
                        source.Content, source.Font, source.FontStyle, source.Align, source.Size, source.Wrap, source.WrapWidth, canvas.Width, canvas.Height);

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
