using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using EditSharp.Components.Channels;
using EditSharp.Components.Clips;
using EditSharp.Components.Media;
using EditSharp.Components.Nodes;
using EditSharp.Components.Transitions;
using EditSharp.History;

namespace EditSharp.Components
{
    /// <summary>What <see cref="TimelineDocument.Deserialize"/> read: the timelines, the media, and anything it couldn't load.</summary>
    /// <param name="Timelines">The timelines, in the order they were saved.</param>
    /// <param name="Media">The media, in the order they were saved; a video's audio stays inside it.</param>
    /// <param name="Warnings">What loaded as a placeholder or was left out, and why; empty when everything loaded.</param>
    public sealed record TimelineDocumentContent(IReadOnlyList<Timeline> Timelines, IReadOnlyList<IMedia> Media, IReadOnlyList<string> Warnings);

    /// <summary>Saves and loads timelines together with the media they read, as one JSON document.</summary>
    /// <remarks>
    /// Timelines, channels, clips and nodes keep their Ids, so wires, link groups, transitions, nested
    /// timelines and media refer to each other by Id, and so can anything outside the document. A kind this
    /// build doesn't know loads as a placeholder (<see cref="MissingNode"/>, <see cref="MissingMedia"/>,
    /// <see cref="MissingTransition"/>) that saves back exactly as it was read. The document carries
    /// <see cref="FormatVersion"/>; an older one is upgraded as it loads and a newer one is refused.
    /// Loading records nothing in history.
    /// </remarks>
    public static class TimelineDocument
    {
        /// <summary>The version of the format this build writes.</summary>
        public const int FormatVersion = 1;

        //steps that bring an older document up to date, run in order from its version on: the one at
        //index i takes version i + 1 to i + 2
        private static readonly List<Action<JsonObject>> Upgrades = [];

        private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

        /// <summary>Saves timelines and media as a JSON document.</summary>
        /// <param name="timelines">The timelines; every timeline one of them nests must be among them.</param>
        /// <param name="media">The media their nodes read; a video's audio is saved inside it.</param>
        /// <returns>The document.</returns>
        public static string Serialize(IEnumerable<Timeline> timelines, IEnumerable<IMedia> media) =>
            ToJson(timelines, media).ToJsonString(Indented);

        /// <summary>Saves timelines and media as a JSON object, for embedding in a larger document.</summary>
        /// <param name="timelines">The timelines; every timeline one of them nests must be among them.</param>
        /// <param name="media">The media their nodes read.</param>
        /// <returns>The document.</returns>
        public static JsonObject ToJson(IEnumerable<Timeline> timelines, IEnumerable<IMedia> media) => new()
        {
            ["formatVersion"] = FormatVersion,
            ["media"] = new JsonArray([.. media.Select(WriteMedia)]),
            ["timelines"] = new JsonArray([.. timelines.Select(WriteTimeline)]),
        };

        /// <summary>Loads a document written by <see cref="Serialize"/>.</summary>
        /// <param name="json">The document.</param>
        /// <returns>What it holds.</returns>
        /// <exception cref="JsonException">The text isn't a document.</exception>
        /// <exception cref="NotSupportedException">The document is from a newer version.</exception>
        public static TimelineDocumentContent Deserialize(string json) =>
            FromJson(JsonNode.Parse(json)?.AsObject() ?? throw new JsonException("The document is empty."));

        /// <summary>Loads a document from a JSON object written by <see cref="ToJson"/>.</summary>
        /// <param name="root">The document; it's upgraded in place when it's from an older version.</param>
        /// <returns>What it holds.</returns>
        /// <exception cref="NotSupportedException">The document is from a newer version.</exception>
        public static TimelineDocumentContent FromJson(JsonObject root)
        {
            Upgrade(root);

            using var _ = Transaction.Suppress();
            var reader = new Reader();
            return reader.Read(root);
        }

        private static void Upgrade(JsonObject root)
        {
            int version = root["formatVersion"]?.GetValue<int>() ?? 1;

            if (version > FormatVersion)
                throw new NotSupportedException($"The document is format version {version}; this build reads up to {FormatVersion}.");

            for (int v = version; v < FormatVersion; v++) Upgrades[v - 1](root);
            root["formatVersion"] = FormatVersion;
        }

        // ---- writing ----

        private static JsonObject WriteMedia(IMedia media) => media is MissingMedia missing
            ? (JsonObject)missing.Saved.DeepClone()
            : JsonNode.Parse(ComponentSerializer.Serialize(media))!.AsObject();

        private static JsonObject WriteTimeline(Timeline timeline) => new()
        {
            ["id"] = timeline.Id,
            ["channels"] = new JsonArray([.. timeline.Channels.Select(WriteChannel)]),
        };

        private static JsonObject WriteChannel(Channel channel)
        {
            JsonObject json = new()
            {
                ["type"] = channel is VideoChannel ? "video" : "audio",
                ["id"] = channel.Id,
                ["name"] = channel.Name,
            };

            if (channel is VideoChannel video) json["blendMode"] = video.BlendMode.ToString();
            if (channel is AudioChannel audio) json["volume"] = audio.Volume;

            json["clips"] = new JsonArray([.. channel.Clips.OrderBy(c => c.Start).Select(WriteClip)]);
            json["transitions"] = new JsonArray([.. channel.Transitions.Select(WriteTransition)]);
            return json;
        }

        /// <summary>A clip as JSON, graph included, for a clipboard or any single-clip store.</summary>
        /// <param name="clip">The clip.</param>
        /// <returns>The clip's JSON; media and nested timelines are referred to by Id.</returns>
        public static JsonObject WriteClip(Clip clip)
        {
            JsonObject json = new()
            {
                ["$kind"] = clip is VideoClip ? "video" : "audio",
                ["id"] = clip.Id,
                ["name"] = clip.Name,
                ["start"] = clip.Start.Ticks,
                ["duration"] = clip.Duration.Ticks,
                ["speed"] = clip.Speed.ToString(),
                ["speedBeforeFreeze"] = clip.SpeedBeforeFreeze.ToString(),
                ["freezeAt"] = clip.FreezeAt.Ticks,
            };

            if (clip.Color is not null) json["color"] = clip.Color;
            if (clip.LinkGroupId is Guid link) json["linkGroup"] = link;
            if (clip is AudioClip audio) json["preservePitch"] = audio.PreservePitch.ToString();

            json["graph"] = WriteGraph(clip.Graph);
            return json;
        }

        private static JsonObject WriteGraph(Graph graph) => new()
        {
            ["domain"] = graph.Domain.ToString(),
            ["nodes"] = new JsonArray([.. graph.Nodes.Select(WriteNode)]),
            ["connections"] = new JsonArray([.. graph.Connections.Select(c => (JsonNode)new JsonObject
            {
                ["from"] = c.FromNodeId,
                ["fromPort"] = c.FromPort,
                ["to"] = c.ToNodeId,
                ["toPort"] = c.ToPort,
            })]),
        };

        private static JsonObject WriteNode(Node node)
        {
            if (node is MissingNode missing) return (JsonObject)missing.Saved.DeepClone();

            JsonObject json = JsonNode.Parse(ComponentSerializer.Serialize(node))!.AsObject();
            json["id"] = node.Id;

            if (node is CompositeNode composite)
            {
                json["name"] = composite.Name;
                json["inner"] = WriteGraph(composite.Inner);
                json["inputs"] = new JsonArray([.. composite.Inputs.Select(e => (JsonNode)new JsonObject { ["node"] = e.Node, ["port"] = e.Port, ["name"] = e.Name })]);
                json["properties"] = new JsonArray([.. composite.ExposedProperties.Select(e => (JsonNode)new JsonObject { ["node"] = e.Node, ["property"] = e.Property, ["name"] = e.Name })]);
            }

            return json;
        }

        private static JsonObject WriteTransition(Transition transition)
        {
            JsonObject json = transition is MissingTransition missing
                ? (JsonObject)missing.Saved.DeepClone()
                : JsonNode.Parse(JsonSerializer.Serialize(transition, ComponentSerializer.Options))!.AsObject();

            json["duration"] = transition.Duration.Ticks;
            json["from"] = transition.From.Id;
            json["to"] = transition.To.Id;
            return json;
        }

        // ---- reading ----

        //one load: what's been read so far, for resolving references by Id
        private sealed class Reader
        {
            private readonly Dictionary<Guid, IMedia> _media = [];
            private readonly Dictionary<Guid, Timeline> _timelines = [];
            private readonly List<string> _warnings = [];

            public TimelineDocumentContent Read(JsonObject root)
            {
                List<IMedia> media = [.. (root["media"]?.AsArray() ?? []).OfType<JsonObject>().Select(ReadMedia)];

                //every timeline exists before any clip loads, so a nested one resolves whatever the order
                List<(Timeline Timeline, JsonObject Json)> timelines = [];
                foreach (JsonObject json in (root["timelines"]?.AsArray() ?? []).OfType<JsonObject>())
                {
                    var timeline = new Timeline { Id = json["id"]?.GetValue<Guid>() ?? Guid.NewGuid() };
                    _timelines[timeline.Id] = timeline;
                    timelines.Add((timeline, json));
                }

                foreach ((Timeline timeline, JsonObject json) in timelines)
                    foreach (JsonObject channel in (json["channels"]?.AsArray() ?? []).OfType<JsonObject>())
                        ReadChannel(timeline, channel);

                return new TimelineDocumentContent([.. timelines.Select(t => t.Timeline)], media, _warnings);
            }

            private IMedia ReadMedia(JsonObject json)
            {
                string kind = json[ComponentSerializer.KindProperty]?.GetValue<string>() ?? "";
                IMedia media;

                try
                {
                    if (!ComponentSerializer.MediaKinds.Any(k => k.Id == kind)) throw new JsonException($"No media kind '{kind}' is installed.");
                    media = ComponentSerializer.DeserializeMedia(json.ToJsonString());
                }
                catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
                {
                    media = new MissingMedia(kind, e.Message, (JsonObject)json.DeepClone());
                    _warnings.Add($"Media '{json["path"]?.GetValue<string>() ?? kind}': {e.Message}");
                }

                _media[media.Id] = media;
                if (media is VideoMedia { Audio: { } audio }) _media[audio.Id] = audio;
                return media;
            }

            private void ReadChannel(Timeline timeline, JsonObject json)
            {
                Channel channel = json["type"]?.GetValue<string>() == "audio"
                    ? new AudioChannel { Volume = json["volume"]?.GetValue<float>() ?? 1f }
                    : new VideoChannel { BlendMode = Enum.TryParse(json["blendMode"]?.GetValue<string>(), out ChannelBlendMode mode) ? mode : default };

                channel.Id = json["id"]?.GetValue<Guid>() ?? Guid.NewGuid();
                if (json["name"]?.GetValue<string>() is { } name) channel.Name = name;
                timeline.AddChannel(channel);

                Dictionary<Guid, Clip> clips = [];
                foreach (JsonObject clipJson in (json["clips"]?.AsArray() ?? []).OfType<JsonObject>())
                {
                    if (ReadClip(clipJson) is not { } clip) continue;

                    if (!channel.IsValidClipType(clip))
                    {
                        _warnings.Add($"Clip '{clip.Name}' is the wrong kind for its channel and was left out.");
                        continue;
                    }

                    channel.Restore(clip);
                    clips[clip.Id] = clip;
                }

                foreach (JsonObject transitionJson in (json["transitions"]?.AsArray() ?? []).OfType<JsonObject>())
                {
                    if (transitionJson["from"]?.GetValue<Guid>() is not { } fromId || !clips.TryGetValue(fromId, out Clip? from) ||
                        transitionJson["to"]?.GetValue<Guid>() is not { } toId || !clips.TryGetValue(toId, out Clip? to))
                    {
                        _warnings.Add("A transition between clips that didn't load was left out.");
                        continue;
                    }

                    Transition transition = ReadTransition(transitionJson);
                    transition.Duration = new Time(transitionJson["duration"]?.GetValue<long>() ?? 0);
                    channel.RestoreTransition(from, to, transition);
                }
            }

            /// <summary>A clip read from <see cref="WriteClip"/>'s JSON, not placed.</summary>
            public Clip? ReadClip(JsonObject json)
            {
                bool video = json["$kind"]?.GetValue<string>() != "audio";
                Graph graph = ReadGraph(json["graph"]?.AsObject(), video ? NodeDomain.Image : NodeDomain.Audio);

                Time start = new(json["start"]?.GetValue<long>() ?? 0);
                Time duration = new(json["duration"]?.GetValue<long>() ?? 0);

                Clip clip;
                try
                {
                    clip = video ? VideoClip.CreateCustom(graph, start, duration) : AudioClip.CreateCustom(graph, start, duration);
                }
                catch (ArgumentException e)
                {
                    _warnings.Add($"Clip '{json["name"]?.GetValue<string>()}': {e.Message}");
                    return null;
                }

                clip.Id = json["id"]?.GetValue<Guid>() ?? Guid.NewGuid();
                if (json["name"]?.GetValue<string>() is { } name) clip.Name = name;
                clip.Color = json["color"]?.GetValue<string>();
                clip.LinkGroupId = json["linkGroup"]?.GetValue<Guid>();

                clip.RestoreTiming(
                    Rational.TryParse(json["speed"]?.GetValue<string>(), out Rational speed) ? speed : Rational.One,
                    Rational.TryParse(json["speedBeforeFreeze"]?.GetValue<string>(), out Rational before) ? before : Rational.One,
                    new Time(json["freezeAt"]?.GetValue<long>() ?? 0));

                if (clip is AudioClip audio && Enum.TryParse(json["preservePitch"]?.GetValue<string>(), out PitchPreservation pitch)) audio.PreservePitch = pitch;

                return clip;
            }

            private Graph ReadGraph(JsonObject? json, NodeDomain fallback)
            {
                NodeDomain domain = Enum.TryParse(json?["domain"]?.GetValue<string>(), out NodeDomain d) ? d : fallback;
                List<JsonObject> nodeJson = [.. (json?["nodes"]?.AsArray() ?? []).OfType<JsonObject>()];
                List<JsonObject> connections = [.. (json?["connections"]?.AsArray() ?? []).OfType<JsonObject>()];

                //every node in its saved place; the ones that don't load wait for their ports, which come from their wires
                Node?[] slots = new Node?[nodeJson.Count];
                List<(int Index, string Kind, string Reason)> missing = [];

                for (int i = 0; i < nodeJson.Count; i++)
                {
                    string kind = nodeJson[i][ComponentSerializer.KindProperty]?.GetValue<string>() ?? "";

                    try
                    {
                        slots[i] = ReadNode(nodeJson[i], kind);
                    }
                    catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
                    {
                        missing.Add((i, kind, e.Message));
                    }
                }

                List<Node> known = [.. slots.OfType<Node>()];
                foreach ((int index, string kind, string reason) in missing)
                {
                    Guid id = nodeJson[index]["id"]?.GetValue<Guid>() ?? Guid.NewGuid();
                    slots[index] = new MissingNode(kind, reason, (JsonObject)nodeJson[index].DeepClone(), PortsFromWires(id, connections, known, domain)) { Id = id };
                    _warnings.Add($"Node '{kind}': {reason}");
                }

                List<Node> loaded = [.. slots.OfType<Node>()];

                OutputNode output = loaded.OfType<OutputNode>().FirstOrDefault()
                    ?? (domain == NodeDomain.Image ? new ImageOutputNode() : new AudioOutputNode());
                Graph graph = Graph.Restore(domain, output);

                foreach (Node node in loaded.Where(n => !ReferenceEquals(n, output)))
                {
                    try { graph.AddNode(node); }
                    catch (InvalidOperationException e) { _warnings.Add($"Node '{node.GetType().Name}': {e.Message}"); }
                }

                foreach (JsonObject c in connections)
                {
                    try
                    {
                        graph.Connect(c["from"]!.GetValue<Guid>(), c["fromPort"]!.GetValue<string>(), c["to"]!.GetValue<Guid>(), c["toPort"]!.GetValue<string>());
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException or NullReferenceException)
                    {
                        _warnings.Add($"A wire couldn't be restored: {e.Message}");
                    }
                }

                return graph;
            }

            private Node ReadNode(JsonObject json, string kind)
            {
                if (!ComponentSerializer.NodeKinds.Any(k => k.Id == kind)) throw new JsonException($"No node kind '{kind}' is installed.");

                Node node;
                if (kind == "composite")
                {
                    Graph inner = ReadGraph(json["inner"]?.AsObject(), NodeDomain.Image);
                    var composite = new CompositeNode(inner, json["name"]?.GetValue<string>() ?? "Custom node");

                    foreach (JsonObject e in (json["inputs"]?.AsArray() ?? []).OfType<JsonObject>())
                        if (inner.Nodes.FirstOrDefault(n => n.Id == e["node"]?.GetValue<Guid>()) is { } target)
                            composite.ExposeInput(target, e["port"]!.GetValue<string>(), e["name"]?.GetValue<string>());

                    foreach (JsonObject e in (json["properties"]?.AsArray() ?? []).OfType<JsonObject>())
                        if (inner.Nodes.FirstOrDefault(n => n.Id == e["node"]?.GetValue<Guid>()) is { } target)
                            composite.ExposeProperty(target, e["property"]!.GetValue<string>(), e["name"]?.GetValue<string>());

                    node = composite;
                }
                else
                {
                    JsonObject properties = (JsonObject)json.DeepClone();
                    properties.Remove("id");
                    node = ComponentSerializer.DeserializeNode(properties.ToJsonString(), Timeline, Media);
                }

                node.Id = json["id"]?.GetValue<Guid>() ?? Guid.NewGuid();
                return node;
            }

            private Transition ReadTransition(JsonObject json)
            {
                string kind = json[ComponentSerializer.KindProperty]?.GetValue<string>() ?? "";

                try
                {
                    if (!ComponentSerializer.TransitionKinds.Any(k => k.Id == kind) || kind == "editsharp.missing")
                        throw new JsonException($"No transition kind '{kind}' is installed.");

                    JsonObject properties = (JsonObject)json.DeepClone();
                    properties.Remove("from");
                    properties.Remove("to");
                    properties.Remove("duration");
                    return JsonSerializer.Deserialize<Transition>(properties.ToJsonString(), ComponentSerializer.Options)!;
                }
                catch (JsonException e)
                {
                    _warnings.Add($"Transition '{kind}': {e.Message}");
                    return new MissingTransition(kind, (JsonObject)json.DeepClone());
                }
            }

            //a missing node's ports: the ones its saved wires use, typed like whatever they connect to
            private static List<NodePort> PortsFromWires(Guid id, List<JsonObject> connections, List<Node> loaded, NodeDomain domain)
            {
                PortType fallback = domain == NodeDomain.Image ? PortType.Image : PortType.Audio;
                List<NodePort> ports = [];

                PortType TypeOf(Guid? other, string? port, PortDirection direction) =>
                    loaded.FirstOrDefault(n => n.Id == other)?.Ports.FirstOrDefault(p => p.Name == port && p.Direction == direction)?.Type ?? fallback;

                foreach (JsonObject c in connections)
                {
                    if (c["to"]?.GetValue<Guid>() == id && c["toPort"]?.GetValue<string>() is { } input && ports.All(p => p.Name != input || p.Direction != PortDirection.Input))
                        ports.Add(new NodePort(input, TypeOf(c["from"]?.GetValue<Guid>(), c["fromPort"]?.GetValue<string>(), PortDirection.Output), PortDirection.Input, optional: true));

                    if (c["from"]?.GetValue<Guid>() == id && c["fromPort"]?.GetValue<string>() is { } output && ports.All(p => p.Name != output || p.Direction != PortDirection.Output))
                        ports.Add(new NodePort(output, TypeOf(c["to"]?.GetValue<Guid>(), c["toPort"]?.GetValue<string>(), PortDirection.Input), PortDirection.Output));
                }

                return ports;
            }

            private Timeline? Timeline(Guid id) => _timelines.GetValueOrDefault(id);

            private IMedia? Media(Guid id) => _media.GetValueOrDefault(id);
        }
    }
}
