using System.Collections.Generic;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Nodes.Math
{
    public enum MathOperation { Add, Subtract, Multiply, Divide, Min, Max }

    /// <summary>
    /// The "base node type for math functions that are universally
    /// applicable" — combines two Value inputs with a plain arithmetic
    /// operation. Domain-universal for the same reason ValueConstantNode
    /// is (only Value ports), so the SAME MathNode class places into either
    /// an Image-domain or an Audio-domain graph.
    ///
    /// On its own this only produces a number — it has no effect on any
    /// signal until it's wired into one of the small set of nodes that
    /// expose an optional Value modulation input (GainNode's "Modulation",
    /// AudioMixNode's "MixAModulation"/"MixBModulation", MergeNode's
    /// "MixModulation" — see each node's own remarks). Wire a
    /// ValueConstantNode (or a whole MathNode chain) into one of those, and
    /// it multiplies against that node's own keyframed value every time
    /// it's evaluated, rather than replacing it outright — so the node's
    /// own curve and a procedurally-computed modulation compose, instead of
    /// one silently overriding the other.
    /// </summary>
    public sealed class MathNode : Node
    {
        MathOperation _operation = MathOperation.Add;
        [Editable("Operation")]
        public MathOperation Operation { get => _operation; set => Transaction.Set(this, ref _operation, value, static (o, v) => o._operation = v); }

        private static readonly NodePort[] StaticPorts =
        [
            new("A", PortType.Value, PortDirection.Input),
            new("B", PortType.Value, PortDirection.Input),
            new("Result", PortType.Value, PortDirection.Output),
        ];

        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override Node Duplicate() => Transaction.Suppressed(() => new MathNode { Enabled = Enabled, Operation = Operation });
    }
}
