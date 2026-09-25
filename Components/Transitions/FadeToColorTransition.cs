using SkiaSharp;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Transitions
{
    /// <summary>Fades out to a colour over the first half, then in from it over the second.</summary>
    public sealed class FadeToColorTransition : Transition
    {
        SKColor _color = SKColors.Black;
        /// <summary>The colour to fade through; black by default.</summary>
        [Editable("Color")]
        public SKColor Color { get => _color; set => Transaction.Set(this, ref _color, value, static (o, v) => o._color = v); }

        /// <inheritdoc/>
        public override Transition Duplicate() => Transaction.Suppressed(() => new FadeToColorTransition { Duration = Duration, Color = Color });
    }
}
