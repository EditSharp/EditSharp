using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using EditSharp.Components.Media;
using EditSharp.Components.Nodes;
using EditSharp.Editing;
using EditSharp.History;

namespace EditSharp.Components
{
    /// <summary>Saves and loads media and nodes as JSON.</summary>
    /// <remarks>
    /// Media and nodes are polymorphic, told apart by "$kind" holding the id from
    /// each kind's <see cref="MediaKindAttribute"/> or <see cref="NodeKindAttribute"/>;
    /// there's no central list to edit when a kind is added. Only kinds in the
    /// EditSharp assembly are found.
    /// <para>
    /// A media saves its properties, its Id and its name as given, with any media it
    /// holds (a video's audio) written inside it under their own Ids. A node
    /// saves exactly its [Editable] properties: never its Id, which a loaded node
    /// gets fresh, nor a composite's inner graph, since graphs aren't saved yet.
    /// Property names are camelCase and TimeSpans are "c"-format strings
    /// (JsonSerializerDefaults.Web). Enums are saved as names, keyframed values
    /// carry their whole track, colours are "#AARRGGBB", a nested Timeline is
    /// saved as its Id, and a media held by a node is saved as its Id.
    /// </para>
    /// <para>
    /// Loading always runs with history suppressed, so a loaded object has no
    /// undo entries of its own.
    /// </para>
    /// </remarks>
    public static class ComponentSerializer
    {
        /// <summary>The JSON property that holds an object's kind id.</summary>
        public const string KindProperty = "$kind";

        internal static readonly IReadOnlyList<(string Id, Type Type)> MediaKinds =
            Discover(typeof(IMedia), static t => t.GetCustomAttribute<MediaKindAttribute>()?.Id, nameof(MediaKindAttribute));

        internal static readonly IReadOnlyList<(string Id, Type Type)> NodeKinds =
            Discover(typeof(Node), static t => t.GetCustomAttribute<NodeKindAttribute>()?.Id, nameof(NodeKindAttribute));

        /// <summary>The options everything is saved and loaded with, read-only.</summary>
        /// <remarks>Use them directly, or chain their TypeInfoResolver into your own, to embed media or nodes in other JSON. A nested Timeline or a shared media can only be loaded through <see cref="Deserialize{T}"/>, which supplies them.</remarks>
        public static JsonSerializerOptions Options { get; } = CreateOptions();

        /// <summary>Saves a media.</summary>
        /// <param name="media">The media to save.</param>
        /// <returns>The media as JSON.</returns>
        public static string Serialize(IMedia media) => JsonSerializer.Serialize(media, Options);

        /// <summary>Saves a node.</summary>
        /// <param name="node">The node to save.</param>
        /// <returns>The node as JSON.</returns>
        public static string Serialize(Node node) => JsonSerializer.Serialize(node, Options);

        /// <summary>Loads a media of any kind.</summary>
        /// <param name="json">The saved media.</param>
        /// <param name="timelines">Finds a nested timeline by its Id, typically in the project being loaded; needed only when the media refers to one.</param>
        /// <returns>The media.</returns>
        /// <exception cref="JsonException">The JSON isn't a media, or refers to a timeline <paramref name="timelines"/> doesn't find.</exception>
        public static IMedia DeserializeMedia(string json, Func<Guid, Timeline?>? timelines = null) => Deserialize<IMedia>(json, timelines);

        /// <summary>Loads a node of any kind.</summary>
        /// <param name="json">The saved node.</param>
        /// <param name="timelines">Finds a nested timeline by its Id, typically in the project being loaded; needed only when the node refers to one.</param>
        /// <param name="media">Finds a shared media by its Id, typically in the project being loaded; needed only when the node refers to one.</param>
        /// <returns>The node, with a new Id.</returns>
        /// <exception cref="JsonException">The JSON isn't a node, or refers to a timeline or media the resolvers don't find.</exception>
        public static Node DeserializeNode(string json, Func<Guid, Timeline?>? timelines = null, Func<Guid, IMedia?>? media = null) =>
            Deserialize<Node>(json, timelines, media);

        /// <summary>Loads an object of a given type.</summary>
        /// <typeparam name="T">The type the object must be.</typeparam>
        /// <param name="json">The saved object.</param>
        /// <param name="timelines">Finds a nested timeline by its Id, typically in the project being loaded; needed only when the object refers to one.</param>
        /// <param name="media">Finds a shared media by its Id, typically in the project being loaded; needed only when the object refers to one.</param>
        /// <returns>The object.</returns>
        /// <exception cref="JsonException">The JSON isn't a <typeparamref name="T"/>, or refers to a timeline or media the resolvers don't find.</exception>
        public static T Deserialize<T>(string json, Func<Guid, Timeline?>? timelines = null, Func<Guid, IMedia?>? media = null) where T : class
        {
            Func<Guid, Timeline?>? previousTimelines = TimelineReferenceJsonConverter.Resolver;
            Func<Guid, IMedia?>? previousMedia = MediaReferenceJsonConverterFactory.Resolver;
            TimelineReferenceJsonConverter.Resolver = timelines;
            MediaReferenceJsonConverterFactory.Resolver = media;

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
                TimelineReferenceJsonConverter.Resolver = previousTimelines;
                MediaReferenceJsonConverterFactory.Resolver = previousMedia;
            }
        }

        //a deep copy of a media with a new Id, made by saving and loading; the media it holds
        //(a video's audio) get new Ids too. History is suppressed
        internal static T Copy<T>(T media) where T : IMedia => Transaction.Suppressed(() =>
        {
            JsonObject json = JsonNode.Parse(Serialize(media))!.AsObject();
            StripIds(json);
            return (T)Deserialize<IMedia>(json.ToJsonString());
        });

        private static void StripIds(JsonObject json)
        {
            json.Remove("id");

            foreach (KeyValuePair<string, JsonNode?> property in json)
                if (property.Value is JsonObject nested) StripIds(nested);
        }

        //a deep copy of a node with a new Id, made by saving and loading; what it refers to is shared
        internal static T Copy<T>(T node, Func<Guid, Timeline?> timelines, Func<Guid, IMedia?> media) where T : Node =>
            Transaction.Suppressed(() => (T)Deserialize<Node>(Serialize(node), timelines, media));

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { AddKinds, ShapeMedia, ShapeNode } },
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

        //what a media saves: its properties, less what nodes hold on it, plus the name exactly as given
        private static void ShapeMedia(JsonTypeInfo info)
        {
            if (!typeof(IMedia).IsAssignableFrom(info.Type) || info.Kind != JsonTypeInfoKind.Object) return;

            for (int i = info.Properties.Count - 1; i >= 0; i--)
                if (info.Properties[i].Name is "name" or "usedBy") info.Properties.RemoveAt(i);

            JsonPropertyInfo name = info.CreateJsonPropertyInfo(typeof(string), "name");
            name.Get = static o => ((IMedia)o).CustomName;
            name.Set = static (o, v) => ((IMedia)o).CustomName = (string?)v;
            info.Properties.Add(name);
        }

        //what a node saves: its [Editable] properties and nothing else; a media among them goes by its Id
        private static void ShapeNode(JsonTypeInfo info)
        {
            if (!typeof(Node).IsAssignableFrom(info.Type) || info.Kind != JsonTypeInfoKind.Object) return;

            for (int i = info.Properties.Count - 1; i >= 0; i--)
            {
                JsonPropertyInfo property = info.Properties[i];

                if (property.AttributeProvider?.IsDefined(typeof(EditableAttribute), inherit: true) != true)
                {
                    info.Properties.RemoveAt(i);
                    continue;
                }

                if (typeof(IMedia).IsAssignableFrom(property.PropertyType))
                    property.CustomConverter = MediaReferenceJsonConverterFactory.Instance;
            }
        }

        //"$kind" on every type a kind derives from, itself included when it's a kind too
        private static void AddKinds(JsonTypeInfo info)
        {
            if (info.Kind != JsonTypeInfoKind.Object) return;

            if (typeof(IMedia).IsAssignableFrom(info.Type)) AddKinds(info, MediaKinds);
            else if (typeof(Node).IsAssignableFrom(info.Type)) AddKinds(info, NodeKinds);
        }

        private static void AddKinds(JsonTypeInfo info, IReadOnlyList<(string Id, Type Type)> kinds)
        {
            var polymorphism = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = KindProperty,
                UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
            };

            foreach ((string id, Type type) in kinds)
            {
                if (info.Type.IsAssignableFrom(type))
                    polymorphism.DerivedTypes.Add(new JsonDerivedType(type, id));
            }

            //a leaf kind on its own needs no discriminator; the types it's held as write one
            if (info.Type.IsAbstract || polymorphism.DerivedTypes.Count > 1)
                info.PolymorphismOptions = polymorphism;
        }

        private static IReadOnlyList<(string, Type)> Discover(Type baseType, Func<Type, string?> idOf, string attributeName)
        {
            var kinds = new List<(string, Type)>();
            var seen = new Dictionary<string, Type>();

            foreach (Type type in baseType.Assembly.GetTypes().Where(baseType.IsAssignableFrom))
            {
                string? id = idOf(type);

                if (id is null)
                {
                    //a concrete kind that can't be saved would only fail later, mid-save
                    if (!type.IsAbstract)
                        throw new InvalidOperationException($"{type.FullName} is a concrete {baseType.Name} without a [{attributeName}].");
                    continue;
                }

                if (type.IsAbstract)
                    throw new InvalidOperationException($"{type.FullName} is abstract but has a [{attributeName}].");

                if (!seen.TryAdd(id, type))
                    throw new InvalidOperationException(
                        $"{baseType.Name} kind '{id}' is claimed by both {seen[id].FullName} and {type.FullName}.");

                kinds.Add((id, type));
            }

            return kinds;
        }
    }

    /// <summary>A media held by a node, saved as its Id; loading finds it through the resolver passed to ComponentSerializer.Deserialize.</summary>
    /// <remarks>Applied per property, never globally, so a media saved on its own is still written in full. A media the resolver can't find fails the load.</remarks>
    internal sealed class MediaReferenceJsonConverterFactory : JsonConverterFactory
    {
        [ThreadStatic] internal static Func<Guid, IMedia?>? Resolver;

        internal static readonly MediaReferenceJsonConverterFactory Instance = new();

        public override bool CanConvert(Type typeToConvert) => typeof(IMedia).IsAssignableFrom(typeToConvert);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert))!;

        private sealed class Converter<T> : JsonConverter<T> where T : IMedia
        {
            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                Guid id = reader.GetGuid();
                IMedia media = Resolver?.Invoke(id) ?? throw new JsonException($"Media {id} is referenced but wasn't supplied to the loader.");
                return media as T ?? throw new JsonException($"Media {id} is a {media.GetType().Name}, not a {typeof(T).Name}.");
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
                writer.WriteStringValue(value.Id);
        }
    }
}
