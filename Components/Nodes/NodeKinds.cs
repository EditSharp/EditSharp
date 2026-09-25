using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using EditSharp.Components.Media;
using EditSharp.Editing;
using EditSharp.History;

namespace EditSharp.Components.Nodes
{
    /// <summary>A kind of node a picker can offer.</summary>
    /// <param name="Id">The id saved files contain.</param>
    /// <param name="DisplayName">The kind's name in pickers.</param>
    /// <param name="Type">The kind's class.</param>
    public sealed record NodeKindInfo(string Id, string DisplayName, Type Type);

    /// <summary>The node kinds pickers offer, and switching a node from one kind to another.</summary>
    public static class NodeKinds
    {
        private static readonly IReadOnlyList<NodeKindInfo> All = [.. ComponentSerializer.NodeKinds.Select(k =>
        {
            NodeKindAttribute attribute = k.Type.GetCustomAttribute<NodeKindAttribute>()!;
            return (Info: new NodeKindInfo(k.Id, attribute.DisplayName ?? k.Type.Name, k.Type), attribute.Listed);
        })
        .Where(k => k.Listed)
        .Select(k => k.Info)
        .OrderBy(k => k.DisplayName, StringComparer.CurrentCulture)];

        /// <summary>The listed kinds that can stand in for a node type.</summary>
        /// <param name="baseType">The type the node must be, such as <see cref="Input.VideoInputNode"/> for the inputs a video graph can take.</param>
        /// <returns>The kinds, by name.</returns>
        public static IReadOnlyList<NodeKindInfo> For(Type baseType) => [.. All.Where(k => baseType.IsAssignableFrom(k.Type))];

        /// <summary>The kind a node is, listed or not.</summary>
        /// <param name="node">The node.</param>
        /// <returns>Its kind; null for a class without a [NodeKind].</returns>
        public static NodeKindInfo? Of(Node node) =>
            All.FirstOrDefault(k => k.Type == node.GetType())
            ?? (node.GetType().GetCustomAttribute<NodeKindAttribute>() is { } a ? new NodeKindInfo(a.Id, a.DisplayName ?? node.GetType().Name, node.GetType()) : null);

        /// <summary>Replaces a node in its graph with a new one of another kind, keeping what carries over.</summary>
        /// <remarks>
        /// Enabled, and every [Editable] property the new kind has under the same name and
        /// type, keyframes included, carry over; the rest takes the kind's defaults. A media
        /// or a nested timeline is shared, not copied. Wires and exposures follow
        /// <see cref="Graph.ReplaceNode"/>. Each change is recorded in the current History
        /// transaction.
        /// </remarks>
        /// <param name="node">The node to replace; it must be in a graph.</param>
        /// <param name="kind">The kind to switch to.</param>
        /// <returns>The new node, in the graph.</returns>
        /// <exception cref="ArgumentException"><paramref name="node"/> isn't in a graph, or <paramref name="kind"/> can't be made from scratch.</exception>
        /// <exception cref="InvalidOperationException">The new kind can't take the node's place; see <see cref="Graph.ReplaceNode"/>.</exception>
        public static Node Switch(Node node, NodeKindInfo kind)
        {
            Graph graph = node.Graph ?? throw new ArgumentException("The node isn't in a graph.", nameof(node));

            Node replacement = Transaction.Suppressed(() => Make(node, kind));
            return graph.ReplaceNode(node, replacement);
        }

        //the old node's saved form, less the properties the new kind doesn't have or has differently, loaded as the new kind
        private static Node Make(Node node, NodeKindInfo kind)
        {
            JsonObject json = JsonNode.Parse(ComponentSerializer.Serialize(node))!.AsObject();
            json[ComponentSerializer.KindProperty] = kind.Id;

            IReadOnlyList<PropertyDescriptor> targets = Inspect.Of(kind.Type);

            foreach (PropertyDescriptor from in Inspect.Of(node))
            {
                PropertyDescriptor? to = targets.FirstOrDefault(d => d.Name == from.Name);

                bool carries = to is not null && !to.IsReadOnly
                    && to.ValueType == from.ValueType
                    && to.IsAnimatable == from.IsAnimatable
                    && to.IsCollection == from.IsCollection
                    && to.ItemType == from.ItemType;

                if (!carries) json.Remove(JsonNamingPolicy.CamelCase.ConvertName(from.Name));
            }

            InputNode? input = node as InputNode;

            try
            {
                return ComponentSerializer.Deserialize<Node>(json.ToJsonString(),
                    input is null ? null : input.ReferencedTimeline,
                    input is null ? null : input.ReferencedMedia);
            }
            catch (NotSupportedException ex)
            {
                throw new ArgumentException($"A {kind.DisplayName} node can't be made from scratch.", nameof(kind), ex);
            }
        }
    }
}
