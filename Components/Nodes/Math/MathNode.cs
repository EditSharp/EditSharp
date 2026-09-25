using System.Collections.Generic;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Nodes.Math
{
    /// <summary>What a <see cref="MathNode"/> does with its two inputs.</summary>
    public enum MathOperation
    {
        /// <summary>A + B.</summary>
        Add,

        /// <summary>A - B.</summary>
        Subtract,

        /// <summary>A × B.</summary>
        Multiply,

        /// <summary>A ÷ B; 0 when B is 0.</summary>
        Divide,

        /// <summary>The smaller of A and B.</summary>
        Min,

        /// <summary>The larger of A and B.</summary>
        Max,
    }

    /// <summary>Combines two numbers.</summary>
    /// <remarks>
    /// Inputs: A and B, each 0 when unconnected. Output: Result. It has only Value
    /// ports, so it goes in video and audio graphs alike. A number only affects a
    /// signal once it's wired into a modulation input (GainNode's Modulation,
    /// AudioMixNode's MixAModulation and MixBModulation, MergeNode's
    /// MixModulation), which multiplies the node's own value by it.
    /// </remarks>
    [NodeKind("math", DisplayName = "Math")]
    public sealed class MathNode : Node
    {
        MathOperation _operation = MathOperation.Add;
        /// <summary>What to do with A and B.</summary>
        [Editable("Operation")]
        public MathOperation Operation { get => _operation; set => Transaction.Set(this, ref _operation, value, static (o, v) => o._operation = v); }

        private static readonly NodePort[] StaticPorts =
        [
            new("A", PortType.Value, PortDirection.Input),
            new("B", PortType.Value, PortDirection.Input),
            new("Result", PortType.Value, PortDirection.Output),
        ];

        /// <inheritdoc/>
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        /// <inheritdoc/>
        public override Node Duplicate() => Transaction.Suppressed(() => new MathNode { Enabled = Enabled, Operation = Operation });
    }
}
