using System.Collections.Generic;
using EditSharp.Components.Clips;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Nodes.Effects
{
    /// <summary>Places the image in the frame: moves, scales and rotates it.</summary>
    /// <remarks>Input: Image. Output: Image. A new video clip's graph has one. Effects before it work on the image before it's placed, effects after it on the placed image; a graph can have several, such as one per branch before a merge.</remarks>
    public sealed class TransformNode : Node
    {
        ClipTransform _transform = new();
        /// <summary>Where and how the image is placed.</summary>
        [Editable("Transform")]
        public ClipTransform Transform { get => _transform; set => Transaction.Set(this, ref _transform, value, static (o, v) => o._transform = v); }

        private static readonly NodePort[] StaticPorts =
        [
            new("Image", PortType.Image, PortDirection.Input),
            new("Image", PortType.Image, PortDirection.Output),
        ];

        /// <inheritdoc/>
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        /// <inheritdoc/>
        public override IEnumerable<IAnimatable> Animatables => Transform.Animatables;

        /// <inheritdoc/>
        public override Node Duplicate() => Transaction.Suppressed(() => new TransformNode { Enabled = Enabled, Transform = Transform.Duplicate() });
    }
}
