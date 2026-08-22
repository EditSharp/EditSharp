using System;
using System.Linq;
using EditSharp.Components.Nodes;
using EditSharp.Components.Nodes.Math;
 
namespace EditSharp.Composite
{
    /// <summary>
    /// Resolves one Value-typed input port at a single instant, by walking
    /// BACKWARD from it through the graph's Connections — ValueConstantNode
    /// leaves evaluate their own Animatable&lt;float&gt;, MathNode recurses
    /// into both its own inputs and applies its Operation.
    ///
    /// Shared between the video evaluator (EffectGraphEvaluatorSk, which
    /// needs one instant per rendered frame) and the audio evaluator
    /// (AudioEffectGraphEvaluator, which needs one instant per automation
    /// block — see its own RenderAutomation) since a Value chain has no
    /// notion of "clip content" at all, only of time — exactly the
    /// "universally applicable" property the user asked a math node have.
    /// </summary>
    internal static class ValueGraphEvaluator
    {
        /// <summary>
        /// Null if `inputPortName` on `node` has nothing connected — the
        /// common case, meaning the caller should fall back to that node's
        /// own keyframed value with no modulation applied.
        /// </summary>
        public static float? TryEvaluateConnectedInput(
            Graph graph, Node node, string inputPortName, TimeSpan time)
        {
            Connection? c = graph.Connections.FirstOrDefault(x => x.ToNodeId == node.Id && x.ToPort == inputPortName);
            if (c == null) return null;
 
            Node? source = graph.Nodes.FirstOrDefault(n => n.Id == c.FromNodeId);
            if (source == null) return null;
 
            return Evaluate(graph, source, c.FromPort, time);
        }
 
        private static float Evaluate(Graph graph, Node node, string outputPortName, TimeSpan time)
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
 