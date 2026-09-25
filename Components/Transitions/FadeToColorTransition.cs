using SkiaSharp;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Transitions
{
    /// <summary>Fades OUT to Color, then fades IN from Color.</summary>
    public sealed class FadeToColorTransition : Transition
    {
        SKColor _color = SKColors.Black;
        [Editable("Color")]
        public SKColor Color { get => _color; set => Transaction.Set(this, ref _color, value, static (o, v) => o._color = v); }

        public override Transition Duplicate() => Transaction.Suppressed(() => new FadeToColorTransition { Duration = Duration, Color = Color });
    }
}
