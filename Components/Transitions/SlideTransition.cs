namespace EditSharp.Components.Transitions
{
    /// <summary>
    /// Both clips translate together across the canvas at Angle degrees.
    /// Replaces the old SlideLeft/Right/Up/Down (0/180/90/270 respectively)
    /// with one continuous parameter — a strict superset, not a different
    /// behaviour at those four angles (verified algebraically).
    /// </summary>
    public sealed class SlideTransition : Transition
    {
        //degrees, Y-up convention matching Position/DropShadowEffect.Offset
        //elsewhere in this codebase: 0 = motion toward +X (right), 90 =
        //motion toward the top of the screen
        public float Angle { get; set; } = 0f;

        public override Transition Duplicate() => new SlideTransition
        {
            Duration = Duration,
            Angle = Angle,
        };
    }
}
