using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Nodes.Effects
{
    /// <summary>Rounds the image's corners, leaving them transparent.</summary>
    /// <remarks>Input: Image. Output: Image.</remarks>
    public sealed class RoundedCornersNode : Node
    {
        Animatable<float> _radius = new(0.1f);
        /// <summary>The corner radius, as a fraction of half the image's shorter side: 1 makes the shorter sides fully round.</summary>
        [Editable("Radius", Min = 0, Max = 0.5, Step = 0.001)]
        public Animatable<float> Radius { get => _radius; set => Transaction.Set(this, ref _radius, value, static (o, v) => o._radius = v); }

        private static readonly NodePort[] StaticPorts =
        [
            new("Image", PortType.Image, PortDirection.Input),
            new("Image", PortType.Image, PortDirection.Output),
        ];

        /// <inheritdoc/>
        public override IReadOnlyList<NodePort> Ports => StaticPorts;

        /// <inheritdoc/>
        public override IEnumerable<IAnimatable> Animatables => [Radius];

        /// <inheritdoc/>
        public override Node Duplicate() => Transaction.Suppressed(() => new RoundedCornersNode { Enabled = Enabled, Radius = Radius.Duplicate() });
    }
}
