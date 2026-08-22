namespace EditSharp.Components.Transitions
{
    /// <summary>Direct crossfade between the two clips — no intermediate colour.</summary>
    public sealed class FadeTransition : Transition
    {
        public override Transition Duplicate() => new FadeTransition { Duration = Duration };
    }
}
 