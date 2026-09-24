using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EditSharp.History;

namespace EditSharp.Components.Sources
{
    /// <summary>A kind a source can be switched to: its saved id, its name in pickers, its class.</summary>
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

        /// <summary>The listed kinds that can stand in for `baseType` (VideoSource or AudioSource).</summary>
        public static IReadOnlyList<SourceKindInfo> For(Type baseType) => [.. All.Where(k => baseType.IsAssignableFrom(k.Type))];

        /// <summary>The kind `source` is, listed or not; null for a class without a [SourceKind].</summary>
        public static SourceKindInfo? Of(Source source) =>
            All.FirstOrDefault(k => k.Type == source.GetType())
            ?? (source.GetType().GetCustomAttribute<SourceKindAttribute>() is { } a ? new SourceKindInfo(a.Id, a.DisplayName ?? source.GetType().Name, source.GetType()) : null);

        /// <summary>
        /// A new source of `kind` with its defaults, keeping `from`'s
        /// Start, Duration and Loop. Assigning it is the caller's edit.
        /// </summary>
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
