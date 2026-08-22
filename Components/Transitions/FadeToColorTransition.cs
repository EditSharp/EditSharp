using SkiaSharp;
 
namespace EditSharp.Components.Transitions
{
    /// <summary>Fades OUT to Color, then fades IN from Color.</summary>
    public sealed class FadeToColorTransition : Transition
    {
        public SKColor Color { get; set; } = SKColors.Black;
 
        public override Transition Duplicate() => new FadeToColorTransition { Duration = Duration, Color = Color };
    }
}
 