using System;

namespace EditSharp.Components.Transitions
{
    /// <summary>Registers a concrete <see cref="Transition"/> for saving and loading under a stable id.</summary>
    /// <param name="id">The id saved files contain; see <see cref="Id"/>.</param>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class TransitionKindAttribute(string id) : Attribute
    {
        /// <summary>The id written into saved data as "$kind".</summary>
        /// <remarks>Saved files depend on it, so it must never change once shipped. The class can be renamed freely.</remarks>
        public string Id { get; } = id;

        /// <summary>The kind's name in editors; null uses the class name.</summary>
        public string? DisplayName { get; init; }
    }
}
