using EditSharp.History;
namespace EditSharp.Components.Transitions
{
    /// <summary>A crossfade: the outgoing clip fades out as the incoming one fades in.</summary>
    public sealed class FadeTransition : Transition
    {
        /// <inheritdoc/>
        public override Transition Duplicate() => Transaction.Suppressed(() => new FadeTransition { Duration = Duration });
    }
}
