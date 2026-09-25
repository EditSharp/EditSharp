using System.Collections.Generic;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Nodes.Effects
{
    /// <summary>How a <see cref="MaskCombineNode"/> combines its two masks.</summary>
    public enum MaskCombineMode
    {
        /// <summary>Everywhere either mask is.</summary>
        Add,

        /// <summary>A, with B cut out of it.</summary>
        Subtract,

        /// <summary>Only where both masks are.</summary>
        Intersect,
    }

    /// <summary>Combines two masks into one.</summary>
    /// <remarks>Inputs: A and B. Output: Result.</remarks>
    public sealed class MaskCombineNode : Node
    {
        MaskCombineMode _mode = MaskCombineMode.Add;
        /// <summary>How the masks combine.</summary>
        [Editable("Mode")]
        public MaskCombineMode Mode { get => _mode; set => Transaction.Set(this, ref _mode, value, static (o, v) => o._mode = v); }

        private static readonly NodePort[] StaticPorts =
        [
            new("A", PortType.Mask, PortDirection.Input),
            new("B", PortType.Mask, PortDirection.Input),
            new("Result", PortType.Mask, PortDirection.Output),
        ];

        /// <inheritdoc/>
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        /// <inheritdoc/>
        public override Node Duplicate() => Transaction.Suppressed(() => new MaskCombineNode { Enabled = Enabled, Mode = Mode });
    }
}
