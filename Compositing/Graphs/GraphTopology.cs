using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components.Nodes;
 
namespace EditSharp.Compositing.Graphs
{
    /// <summary>
    /// The Kahn's-algorithm topological ordering shared by BOTH real graph
    /// evaluators — ImageGraphEvaluator (Image/Mask domain) and
    /// AudioGraphEvaluator (Audio domain). Extracted out of
    /// ImageGraphEvaluator (which used to have its own private copy) so
    /// the walking algorithm itself has exactly one implementation shared
    /// across both domains, rather than two copies that could drift apart —
    /// the two evaluators differ only in what they DO with each node once
    /// visited, never in how the visit order is computed.
    /// </summary>
    internal static class GraphTopology
    {
        public static List<Node> Order(Graph graph)
        {
            var inDegree = graph.Nodes.ToDictionary(n => n.Id, _ => 0);
            foreach (Connection c in graph.Connections) inDegree[c.ToNodeId]++;
 
            var byId = graph.Nodes.ToDictionary(n => n.Id);
            var queue = new Queue<Node>(graph.Nodes.Where(n => inDegree[n.Id] == 0));
            var order = new List<Node>();
 
            while (queue.Count > 0)
            {
                Node node = queue.Dequeue();
                order.Add(node);
 
                foreach (Connection c in graph.Connections.Where(c => c.FromNodeId == node.Id))
                {
                    if (--inDegree[c.ToNodeId] == 0) queue.Enqueue(byId[c.ToNodeId]);
                }
            }
 
            if (order.Count != graph.Nodes.Count)
                throw new InvalidOperationException(
                    "Graph has a cycle — Graph.Connect should have prevented this.");
 
            return order;
        }
    }
}
 