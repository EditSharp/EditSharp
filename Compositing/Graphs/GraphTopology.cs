using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components.Nodes;

namespace EditSharp.Compositing.Graphs
{
    //the evaluation order graph evaluators share (Kahn's algorithm)
    internal static class GraphTopology
    {
        /// <summary>
        /// The nodes that feed the graph's Output, in an order where every node
        /// comes after everything it reads from. Nodes that don't reach the
        /// Output (added but not wired yet) are left out, and connections to
        /// or from a node the graph doesn't hold are ignored, so any state a
        /// graph can be in mid-edit evaluates.
        /// </summary>
        public static List<Node> Order(Graph graph)
        {
            var byId = graph.Nodes.ToDictionary(n => n.Id);
            List<Connection> links = [.. graph.Connections.Where(c => byId.ContainsKey(c.FromNodeId) && byId.ContainsKey(c.ToNodeId))];

            //everything upstream of the Output
            var feeding = new HashSet<Guid> { graph.OutputNode.Id };
            var pending = new Stack<Guid>(feeding);
            while (pending.Count > 0)
            {
                Guid id = pending.Pop();
                foreach (Connection c in links.Where(c => c.ToNodeId == id))
                {
                    if (feeding.Add(c.FromNodeId)) pending.Push(c.FromNodeId);
                }
            }

            links.RemoveAll(c => !feeding.Contains(c.ToNodeId));

            var inDegree = feeding.ToDictionary(id => id, _ => 0);
            foreach (Connection c in links) inDegree[c.ToNodeId]++;

            var queue = new Queue<Node>(feeding.Where(id => inDegree[id] == 0).Select(id => byId[id]));
            var order = new List<Node>();

            while (queue.Count > 0)
            {
                Node node = queue.Dequeue();
                order.Add(node);

                foreach (Connection c in links.Where(c => c.FromNodeId == node.Id))
                {
                    if (--inDegree[c.ToNodeId] == 0) queue.Enqueue(byId[c.ToNodeId]);
                }
            }

            if (order.Count != feeding.Count)
                throw new InvalidOperationException("Graph has a cycle; Graph.Connect should have prevented this.");

            return order;
        }
    }
}
