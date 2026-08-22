using System.Collections.Generic;
 
namespace EditSharp.Components.Nodes
{
    /// <summary>Feeds the renderer. Mandatory, not removable — the one fixed anchor left in an Image-domain graph.</summary>
    public sealed class ImageOutputNode : OutputNode
    {
        private static readonly NodePort[] StaticPorts = [new("Image", PortType.Image, PortDirection.Input)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override Node Duplicate() => new ImageOutputNode();
    }
}
 