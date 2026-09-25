using EditSharp.History;
using EditSharp.Editing;
namespace EditSharp.Components.Transitions
{
    /// <summary>Both clips translate together across the canvas at Angle degrees.</summary>
    public sealed class SlideTransition : Transition
    {
        //degrees, Y-up convention matching Position/DropShadowNode.Offset elsewhere in this schema
        float _angle = 0f;
        [Editable("Angle", Editor = PropertyEditor.Angle)]
        public float Angle { get => _angle; set => Transaction.Set(this, ref _angle, value, static (o, v) => o._angle = v); }

        public override Transition Duplicate() => Transaction.Suppressed(() => new SlideTransition { Duration = Duration, Angle = Angle });
    }
}
