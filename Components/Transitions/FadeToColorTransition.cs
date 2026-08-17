using SkiaSharp;

namespace EditSharp.Components.Transitions
{
    /// <summary>
    /// Fades OUT to Color, then fades IN from Color. Replaces the old
    /// FadeBlack/FadeWhite/FadeGrays — same behaviour, colour is a real
    /// parameter instead of three hardcoded enum members.
    /// </summary>
    public sealed class FadeToColorTransition : Transition
    {
        public SKColor Color { get; set; } = SKColors.Black;

        public override Transition Duplicate() => new FadeToColorTransition
        {
            Duration = Duration,
            Color = Color,
        };
    }
}
