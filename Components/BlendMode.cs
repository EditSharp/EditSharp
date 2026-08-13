namespace EditSharp.Components
{
    /// <summary>
    /// How a channel is composited onto everything beneath it.
    ///
    /// Deliberately its own enum rather than SkiaSharp's SKBlendMode. Skia has
    /// around thirty modes and ffmpeg's `blend` filter only partly overlaps with
    /// them — the non-separable modes (hue, saturation, colour, luminosity) and the
    /// Porter-Duff compositing operators have no counterpart at all. Exposing
    /// SKBlendMode would advertise modes that cannot be rendered; every value here
    /// maps to a real ffmpeg mode.
    /// </summary>
    public enum BlendMode
    {
        //plain alpha compositing — the channel simply sits on top.
        //takes the cheaper `overlay` path rather than going through `blend`
        Normal,

        Multiply,
        Screen,
        Overlay,
        Darken,
        Lighten,
        ColorDodge,
        ColorBurn,
        HardLight,
        SoftLight,
        Difference,
        Exclusion,
        Addition,
        Subtract,
        Divide,
        Average,
        Negation,
        Xor,
    }
}
