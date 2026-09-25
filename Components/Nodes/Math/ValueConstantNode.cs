using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Nodes.Math
{
    /// <summary>A keyframeable number, for a <see cref="MathNode"/> or a modulation input.</summary>
    /// <remarks>Output: Value. It has only Value ports, so it goes in video and audio graphs alike.</remarks>
    [NodeKind("value-constant", DisplayName = "Value")]
    public sealed class ValueConstantNode : Node
    {
        Animatable<float> _value = new(0f);
        /// <summary>The number.</summary>
        [Editable("Value")]
        public Animatable<float> Value { get => _value; set => Transaction.Set(this, ref _value, value, static (o, v) => o._value = v); }

        private static readonly NodePort[] StaticPorts = [new("Value", PortType.Value, PortDirection.Output)];
        /// <inheritdoc/>
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        /// <inheritdoc/>
        public override IEnumerable<IAnimatable> Animatables => [Value];

        /// <inheritdoc/>
        public override Node Duplicate() => Transaction.Suppressed(() => new ValueConstantNode { Enabled = Enabled, Value = Value.Duplicate() });
    }
}
