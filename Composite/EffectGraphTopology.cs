using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components.Effects;
 
namespace EditSharp.Composite
{
    /// <summary>
    /// The Kahn's-algorithm topological ordering shared by BOTH real graph
    /// evaluators — EffectGraphEvaluatorSk (Image/Mask domain) and
    /// AudioEffectGraphEvaluator (Audio domain). Extracted out of
    /// EffectGraphEvaluatorSk (which used to have its own private copy) so
    /// the walking algorithm itself has exactly one implementation shared
    /// across both domains, rather than two copies that could drift apart —
    /// the two evaluators differ only in what they DO with each node once
    /// visited, never in how the visit order is computed.
    /// </summary>
    internal static class EffectGraphTopology
    {
        public static List<EffectNode> Order(EffectGraph graph)
        {
            var inDegree = graph.Nodes.ToDictionary(n => n.Id, _ => 0);
            foreach (Connection c in graph.Connections) inDegree[c.ToNodeId]++;
 
            var byId = graph.Nodes.ToDictionary(n => n.Id);
            var queue = new Queue<EffectNode>(graph.Nodes.Where(n => inDegree[n.Id] == 0));
            var order = new List<EffectNode>();
 
            while (queue.Count > 0)
            {
                EffectNode node = queue.Dequeue();
                order.Add(node);
 
                foreach (Connection c in graph.Connections.Where(c => c.FromNodeId == node.Id))
                {
                    if (--inDegree[c.ToNodeId] == 0) queue.Enqueue(byId[c.ToNodeId]);
                }
            }
 
            if (order.Count != graph.Nodes.Count)
                throw new InvalidOperationException(
                    "EffectGraph has a cycle — EffectGraph.Connect should have prevented this.");
 
            return order;
        }
    }
}
 