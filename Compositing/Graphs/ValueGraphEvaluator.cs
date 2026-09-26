using System;
using System.Linq;
using EditSharp.Components.Nodes;
using EditSharp.Components.Nodes.Math;

namespace EditSharp.Compositing.Graphs
{
    //resolves a Value input at one instant by walking back through its connections: a ValueConstantNode
    //evaluates its keyframed value, a MathNode evaluates its inputs and applies its operation.
    //Video evaluates it once per frame, audio once per automation step
    internal static class ValueGraphEvaluator
    {
        //null when nothing enabled is connected, so the caller uses the node's own value unmodulated
        public static float? TryEvaluateConnectedInput(
            Graph graph, Node node, string inputPortName, Time time)
        {
            Connection? c = graph.Connections.FirstOrDefault(x => x.ToNodeId == node.Id && x.ToPort == inputPortName);
            if (c == null) return null;

            //a disabled Value node feeds nothing
            Node? source = graph.Nodes.FirstOrDefault(n => n.Id == c.FromNodeId);
            if (source == null || !source.Enabled) return null;

            return Evaluate(graph, source, c.FromPort, time);
        }

        private static float Evaluate(Graph graph, Node node, string outputPortName, Time time)
        {
            switch (node)
            {
                case ValueConstantNode constant:
                    return constant.Value.Evaluate(time);

                case MathNode math:
                {
                    float a = TryEvaluateConnectedInput(graph, math, "A", time) ?? 0f;
                    float b = TryEvaluateConnectedInput(graph, math, "B", time) ?? 0f;

                    return math.Operation switch
                    {
                        MathOperation.Add => a + b,
                        MathOperation.Subtract => a - b,
                        MathOperation.Multiply => a * b,
                        MathOperation.Divide => Math.Abs(b) < 1e-9f ? 0f : a / b,
                        MathOperation.Min => Math.Min(a, b),
                        MathOperation.Max => Math.Max(a, b),
                        _ => throw new NotSupportedException($"Unknown MathOperation: {math.Operation}"),
                    };
                }

                default:
                    throw new NotSupportedException(
                        $"ValueGraphEvaluator has no dispatch for {node.GetType().Name} on port '{outputPortName}'.");
            }
        }
    }
}
