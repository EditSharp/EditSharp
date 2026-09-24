using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;
 
namespace EditSharp.Components.Nodes.Effects
{
    public sealed class BlurNode : Node
    {
        Animatable<float> _radius = new(0f);
        [Editable("Radius", Min = 0, Max = 0.5, Step = 0.001, Unit = "of width", Frame = FrameMeasure.Width)]
        public Animatable<float> Radius { get => _radius; set => Transaction.Set(this, ref _radius, value, static (o, v) => o._radius = v); }
 
        private static readonly NodePort[] StaticPorts =
        [
            new("Image", PortType.Image, PortDirection.Input),
            new("Mask", PortType.Mask, PortDirection.Input, optional: true),
            new("Image", PortType.Image, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override IEnumerable<IAnimatable> Animatables => [Radius];

        public override Node Duplicate() => Transaction.Suppressed(() => new BlurNode { Enabled = Enabled, Radius = Radius.Duplicate() });
    }
}
 