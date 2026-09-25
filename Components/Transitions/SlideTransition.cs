using EditSharp.History;
using EditSharp.Editing;
namespace EditSharp.Components.Transitions
{
    /// <summary>The incoming clip pushes the outgoing one out of the frame.</summary>
    public sealed class SlideTransition : Transition
    {
        float _angle = 0f;
        /// <summary>Which way the clips move, in degrees anticlockwise from rightwards: 0 is right, 90 is up.</summary>
        [Editable("Angle", Editor = PropertyEditor.Angle)]
        public float Angle { get => _angle; set => Transaction.Set(this, ref _angle, value, static (o, v) => o._angle = v); }

        /// <inheritdoc/>
        public override Transition Duplicate() => Transaction.Suppressed(() => new SlideTransition { Duration = Duration, Angle = Angle });
    }
}
