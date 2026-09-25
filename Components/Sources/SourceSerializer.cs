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
    /// <summary>Saves and loads sources as JSON.</summary>
    /// <remarks>
    /// Every abstract source type (<see cref="Source"/>, <see cref="Video.VideoSource"/>,
    /// <see cref="Audio.AudioSource"/>) is polymorphic, told apart by "$kind" holding
    /// the id from each kind's <see cref="SourceKindAttribute"/>; there's no central
    /// list to edit when a kind is added. Only kinds in the EditSharp assembly are found.
    /// <para>
    /// Property names are camelCase and TimeSpans are "c"-format strings
    /// (JsonSerializerDefaults.Web). Enums are saved as names, keyframed values
    /// carry their whole track, colours are "#AARRGGBB", and a nested Timeline is
    /// saved as its Id.
    /// </para>
    /// <para>
    /// Loading always runs with history suppressed, so a loaded source has no
    /// undo entries of its own.
    /// </para>
    /// </remarks>
    public static class SourceSerializer
    {
        /// <summary>The JSON property that holds a source's kind id.</summary>
        public const string KindProperty = "$kind";

        internal static readonly IReadOnlyList<(string Id, Type Type)> Kinds = Discover(typeof(Source).Assembly);

        /// <summary>The options sources are saved and loaded with, read-only.</summary>
        /// <remarks>Use them directly, or chain their TypeInfoResolver into your own, to embed sources in other JSON. A nested Timeline can only be loaded through <see cref="Deserialize{T}"/>, which supplies the timelines.</remarks>
        public static JsonSerializerOptions Options { get; } = CreateOptions();

        /// <summary>Saves a source.</summary>
        /// <param name="source">The source to save.</param>
        /// <returns>The source as JSON.</returns>
        public static string Serialize(Source source) => JsonSerializer.Serialize(source, Options);

        /// <summary>Loads a source of any kind.</summary>
        /// <param name="json">The saved source.</param>
        /// <param name="timelines">Finds a nested timeline by its Id, typically in the project being loaded; needed only when a source refers to one.</param>
        /// <returns>The source.</returns>
        /// <exception cref="JsonException">The JSON isn't a source, or refers to a timeline <paramref name="timelines"/> doesn't find.</exception>
        public static Source Deserialize(string json, Func<Guid, Timeline?>? timelines = null) => Deserialize<Source>(json, timelines);

        /// <summary>Loads a source of a given type.</summary>
        /// <typeparam name="T">The type the source must be.</typeparam>
        /// <param name="json">The saved source.</param>
        /// <param name="timelines">Finds a nested timeline by its Id, typically in the project being loaded; needed only when a source refers to one.</param>
        /// <returns>The source.</returns>
        /// <exception cref="JsonException">The JSON isn't a <typeparamref name="T"/>, or refers to a timeline <paramref name="timelines"/> doesn't find.</exception>
        public static T Deserialize<T>(string json, Func<Guid, Timeline?>? timelines = null) where T : Source
        {
            Func<Guid, Timeline?>? previous = TimelineReferenceJsonConverter.Resolver;
            TimelineReferenceJsonConverter.Resolver = timelines;

            try
            {
                using (Transaction.Suppress())
                {
                    return JsonSerializer.Deserialize<T>(json, Options)
                        ?? throw new JsonException($"Expected a {typeof(T).Name}, got null.");
                }
            }
            finally
            {
                TimelineReferenceJsonConverter.Resolver = previous;
            }
        }

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { AddKinds, SkipAnimatablesList } },
                Converters =
                {
                    new JsonStringEnumConverter(),
                    new AnimatableJsonConverterFactory(),
                    new SKColorJsonConverter(),
                    new Vector2JsonConverter(),
                    new TimelineReferenceJsonConverter(),
                },
            };

            options.MakeReadOnly();
            return options;
        }

        //Animatables only lists values the kind's own properties already save; overrides don't inherit [JsonIgnore]
        private static void SkipAnimatablesList(JsonTypeInfo info)
        {
            if (!typeof(Source).IsAssignableFrom(info.Type) || info.Kind != JsonTypeInfoKind.Object) return;

            for (int i = info.Properties.Count - 1; i >= 0; i--)
                if (info.Properties[i].Name == "animatables") info.Properties.RemoveAt(i);
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
