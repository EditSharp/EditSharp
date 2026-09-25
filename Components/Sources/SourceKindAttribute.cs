using System;

namespace EditSharp.Components.Sources
{
    /// <summary>
    /// Registers a concrete Source for serialization under a stable id. The id
    /// is written into saved data as "$kind", so it must never change once
    /// shipped; rename the class freely, never the id. DisplayName is what
    /// editors show; Listed false keeps a kind out of pickers (examples,
    /// unfinished kinds) while it still loads.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class SourceKindAttribute(string id) : Attribute
    {
        public string Id { get; } = id;
        public string? DisplayName { get; init; }
        public bool Listed { get; init; } = true;
    }
}
