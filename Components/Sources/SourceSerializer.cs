using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using EditSharp.History;

namespace EditSharp.Components.Sources
{
    /// <summary>
    /// Saves and loads sources as JSON. Every abstract Source type (Source,
    /// VideoSource, AudioSource, ...) is polymorphic, discriminated by "$kind"
    /// with the id from each concrete kind's [SourceKind]; no central list to
    /// edit when a kind is added. Property names are camelCase and TimeSpans
    /// are "c"-format strings (JsonSerializerDefaults.Web).
    ///
    /// Consumers embedding sources in their own JSON use Options directly (or
    /// chain its TypeInfoResolver into theirs); Serialize/Deserialize are the
    /// standalone shortcut. Loading always runs with history suppressed, so a
    /// deserialized source arrives with no undo entries of its own.
    ///
    /// Only kinds in this assembly are discovered for now (source kinds are
    /// internal to EditSharp); registering external assemblies belongs with
    /// opening the source contract up.
    /// </summary>
    public static class SourceSerializer
    {
        public const string KindProperty = "$kind";

        private static readonly IReadOnlyList<(string Id, Type Type)> Kinds = Discover(typeof(Source).Assembly);

        public static JsonSerializerOptions Options { get; } = CreateOptions();

        public static string Serialize(Source source) => JsonSerializer.Serialize(source, Options);

        public static Source Deserialize(string json) => Deserialize<Source>(json);

        public static T Deserialize<T>(string json) where T : Source
        {
            using (Transaction.Suppress())
            {
                return JsonSerializer.Deserialize<T>(json, Options)
                    ?? throw new JsonException($"Expected a {typeof(T).Name}, got null.");
            }
        }

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { AddKinds } },
            };

            options.MakeReadOnly();
            return options;
        }

        private static void AddKinds(JsonTypeInfo info)
        {
            if (!info.Type.IsAbstract || !typeof(Source).IsAssignableFrom(info.Type)) return;

            var polymorphism = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = KindProperty,
                UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
            };

            foreach ((string id, Type type) in Kinds)
            {
                if (info.Type.IsAssignableFrom(type))
                    polymorphism.DerivedTypes.Add(new JsonDerivedType(type, id));
            }

            info.PolymorphismOptions = polymorphism;
        }

        private static IReadOnlyList<(string, Type)> Discover(Assembly assembly)
        {
            var kinds = new List<(string, Type)>();
            var seen = new Dictionary<string, Type>();

            foreach (Type type in assembly.GetTypes().Where(t => typeof(Source).IsAssignableFrom(t)))
            {
                SourceKindAttribute? kind = type.GetCustomAttribute<SourceKindAttribute>();

                if (kind is null)
                {
                    //a concrete kind that can't be saved would only fail later, mid-save
                    if (!type.IsAbstract)
                        throw new InvalidOperationException($"{type.FullName} is a concrete Source without a [SourceKind].");
                    continue;
                }

                if (type.IsAbstract)
                    throw new InvalidOperationException($"{type.FullName} is abstract but has a [SourceKind].");

                if (!seen.TryAdd(kind.Id, type))
                    throw new InvalidOperationException(
                        $"Source kind '{kind.Id}' is claimed by both {seen[kind.Id].FullName} and {type.FullName}.");

                kinds.Add((kind.Id, type));
            }

            return kinds;
        }
    }
}
