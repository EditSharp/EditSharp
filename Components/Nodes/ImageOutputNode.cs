using System.Collections.Generic;

namespace EditSharp.Components.Nodes
{
    /// <summary>The output of a video clip's graph: the image the clip draws.</summary>
    /// <remarks>Input: Image.</remarks>
    [NodeKind("image-output", DisplayName = "Image output", Listed = false)]
    public sealed class ImageOutputNode : OutputNode
    {
        private static readonly NodePort[] StaticPorts = [new("Image", PortType.Image, PortDirection.Input)];
        /// <inheritdoc/>
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        /// <inheritdoc/>
        public override Node Duplicate() => new ImageOutputNode();
    }
}
