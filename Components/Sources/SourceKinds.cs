using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EditSharp.History;

namespace EditSharp.Components.Sources
{
    /// <summary>A kind a source can be switched to.</summary>
    /// <param name="Id">The id saved files contain.</param>
    /// <param name="DisplayName">The kind's name in pickers.</param>
    /// <param name="Type">The kind's class.</param>
    public sealed record SourceKindInfo(string Id, string DisplayName, Type Type);

    /// <summary>The source kinds pickers offer, and switching a source from one kind to another.</summary>
    public static class SourceKinds
    {
        private static readonly IReadOnlyList<SourceKindInfo> All = [.. SourceSerializer.Kinds.Select(k =>
        {
            SourceKindAttribute attribute = k.Type.GetCustomAttribute<SourceKindAttribute>()!;
            return (Info: new SourceKindInfo(k.Id, attribute.DisplayName ?? k.Type.Name, k.Type), attribute.Listed);
        })
        .Where(k => k.Listed)
        .Select(k => k.Info)
        //files first, the rest by name
        .OrderBy(k => !typeof(IFileBackedSource).IsAssignableFrom(k.Type))
        .ThenBy(k => k.DisplayName, StringComparer.CurrentCulture)];

        /// <summary>The listed kinds that can stand in for a source type.</summary>
        /// <param name="baseType">The type the source must be, such as <see cref="Video.VideoSource"/> or <see cref="Audio.AudioSource"/>.</param>
        /// <returns>File-backed kinds first, then the rest by name.</returns>
        public static IReadOnlyList<SourceKindInfo> For(Type baseType) => [.. All.Where(k => baseType.IsAssignableFrom(k.Type))];

        /// <summary>The kind a source is, listed or not.</summary>
        /// <param name="source">The source.</param>
        /// <returns>Its kind; null for a class without a [SourceKind].</returns>
        public static SourceKindInfo? Of(Source source) =>
            All.FirstOrDefault(k => k.Type == source.GetType())
            ?? (source.GetType().GetCustomAttribute<SourceKindAttribute>() is { } a ? new SourceKindInfo(a.Id, a.DisplayName ?? source.GetType().Name, source.GetType()) : null);

        /// <summary>A new source of another kind, with that kind's defaults and the old source's <see cref="Source.Start"/>, <see cref="Source.Duration"/> and <see cref="Source.Loop"/>.</summary>
        /// <remarks>Nothing is recorded in history; assigning the new source is the caller's edit.</remarks>
        /// <param name="from">The source being replaced.</param>
        /// <param name="kind">The kind to switch to.</param>
        /// <returns>The new source.</returns>
        public static Source Switch(Source from, SourceKindInfo kind) => Transaction.Suppressed(() =>
        {
            var source = (Source)Activator.CreateInstance(kind.Type)!;
            source.Start = from.Start;
            source.Duration = from.Duration;
            source.Loop = from.Loop;
            return source;
        });
    }
}
