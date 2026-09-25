using System.Collections.Generic;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Nodes.Effects
{
    public enum MaskCombineMode { Add, Subtract, Intersect }

    public sealed class MaskCombineNode : Node
    {
        MaskCombineMode _mode = MaskCombineMode.Add;
        [Editable("Mode")]
        public MaskCombineMode Mode { get => _mode; set => Transaction.Set(this, ref _mode, value, static (o, v) => o._mode = v); }

        private static readonly NodePort[] StaticPorts =
        [
            new("A", PortType.Mask, PortDirection.Input),
            new("B", PortType.Mask, PortDirection.Input),
            new("Result", PortType.Mask, PortDirection.Output),
        ];

        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override Node Duplicate() => Transaction.Suppressed(() => new MaskCombineNode { Enabled = Enabled, Mode = Mode });
    }
}
