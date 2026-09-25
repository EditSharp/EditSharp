using System.Collections.Generic;
using System.Linq;
using EditSharp.Components.Nodes;
using EditSharp.Components.Nodes.Effects;
 
namespace EditSharp.Compositing
{
    /// <summary>
    /// Small shared graph-walking helper used by both ContentPreparation
    /// (deciding a VideoSourceNode's decode target size) and ClipContentSource
    /// (the same decision, at decoder-open time). Not part of Graph itself
    /// — this is a RENDER-side heuristic, not a structural graph invariant.
    ///
    /// "CLIPS ARE GRAPHS" REWRITE: TransformNode's data (ClipTransform) now
    /// lives on the node itself rather than on a owning VisualClip, and a graph
    /// can have more than one TransformNode (e.g. one per branch before a
    /// MergeNode). Deciding which TransformNode "belongs" to a given
    /// VideoSourceNode for sizing purposes is therefore ambiguous in a fully
    /// general multi-branch graph. The approximation taken here — first
    /// TransformNode reachable by walking forward from the InputNode — is
    /// exactly correct for every "normal/default" graph (CreateVideoGraph's
    /// InputNode -> TintNode -> TransformNode -> Output shape) and a
    /// documented, reasoned approximation for a hand-built graph with more
    /// than one TransformNode downstream of a single input. This is a known,
    /// flagged limitation, not silently swept under the rug.
    /// </summary>
    internal static class DecodeSizeHeuristics
    {
        /// <summary>
        /// Breadth-first walk forward from `start` along the graph's own
        /// Connections; returns the first TransformNode reached, or null if
        /// none is downstream at all (a pure Value-domain branch, or a
        /// custom graph with no TransformNode whatsoever).
        /// </summary>
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
 