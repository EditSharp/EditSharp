using System.Text.Json.Nodes;

namespace EditSharp.Components.Transitions
{
    /// <summary>A transition a project names but this build can't load; it plays as a crossfade.</summary>
    /// <remarks>It keeps the transition's saved JSON word for word, so saving the project writes it back unchanged.</remarks>
    [TransitionKind("editsharp.missing", DisplayName = "Missing transition")]
    public sealed class MissingTransition : Transition
    {
        /// <summary>The kind id the project names.</summary>
        public string Kind { get; }

        //the transition exactly as it was saved
        internal JsonObject Saved { get; }

        internal MissingTransition(string kind, JsonObject saved)
        {
            Kind = kind;
            Saved = saved;
        }

        /// <inheritdoc/>
        public override Transition Duplicate() => new MissingTransition(Kind, (JsonObject)Saved.DeepClone()) { Duration = Duration };
    }
}
