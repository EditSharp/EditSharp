namespace EditSharp.Components.Transitions
{
    /// <summary>Both clips translate together across the canvas at Angle degrees.</summary>
    public sealed class SlideTransition : Transition
    {
        //degrees, Y-up convention matching Position/DropShadowNode.Offset elsewhere in this schema
        public float Angle { get; set; } = 0f;
 
        public override Transition Duplicate() => new SlideTransition { Duration = Duration, Angle = Angle };
    }
}
 