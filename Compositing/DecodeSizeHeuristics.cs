using System.Collections.Generic;
using System.Linq;
using EditSharp.Components.Nodes;
using EditSharp.Components.Nodes.Effects;

namespace EditSharp.Compositing
{
    //which TransformNode sizes a source's decode: the first one downstream of it. Exact for the default
    //graph (input, tint, transform, output); an approximation when a source feeds several transforms
    internal static class DecodeSizeHeuristics
    {
        //breadth-first from `start` along the connections; null when no TransformNode is downstream
        public static TransformNode? FindDownstreamTransform(Graph graph, Node start)
        {
            var visited = new HashSet<System.Guid> { start.Id };
            var queue = new Queue<System.Guid>();
            queue.Enqueue(start.Id);

            while (queue.Count > 0)
            {
                System.Guid current = queue.Dequeue();

                foreach (Connection c in graph.Connections.Where(x => x.FromNodeId == current))
                {
                    if (!visited.Add(c.ToNodeId)) continue;

                    Node? node = graph.Nodes.FirstOrDefault(n => n.Id == c.ToNodeId);
                    if (node is TransformNode transform) return transform;
                    if (node != null) queue.Enqueue(node.Id);
                }
            }

            return null;
        }
    }
}
