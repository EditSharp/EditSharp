using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Nodes.Effects
{
    /// <summary>A Gaussian blur.</summary>
    /// <remarks>Inputs: Image, and an optional Mask; with a mask, the blur applies only where the mask is. Output: Image.</remarks>
    [NodeKind("blur", DisplayName = "Blur")]
    public sealed class BlurNode : Node
    {
        Animatable<float> _radius = new(0f);
        /// <summary>How far the blur spreads (its standard deviation), as a fraction of the frame's width.</summary>
        [Editable("Radius", Min = 0, Max = 0.5, Step = 0.001, Unit = "of width", Frame = FrameMeasure.Width)]
        public Animatable<float> Radius { get => _radius; set => Transaction.Set(this, ref _radius, value, static (o, v) => o._radius = v); }

        private static readonly NodePort[] StaticPorts =
        [
            new("Image", PortType.Image, PortDirection.Input),
            new("Mask", PortType.Mask, PortDirection.Input, optional: true),
            new("Image", PortType.Image, PortDirection.Output),
        ];

        /// <inheritdoc/>
        public override IReadOnlyList<NodePort> Ports => StaticPorts;

        /// <inheritdoc/>
        public override IEnumerable<IAnimatable> Animatables => [Radius];

        /// <inheritdoc/>
        public override Node Duplicate() => Transaction.Suppressed(() => new BlurNode { Enabled = Enabled, Radius = Radius.Duplicate() });
    }
}
