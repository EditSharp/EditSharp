using System.Collections.Generic;

namespace EditSharp.Components.Nodes
{
    /// <summary>Feeds the audio mixer. Mandatory, not removable — the one fixed anchor left in an Audio-domain graph.</summary>
    public sealed class AudioOutputNode : OutputNode
    {
        private static readonly NodePort[] StaticPorts = [new("Audio", PortType.Audio, PortDirection.Input)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override Node Duplicate() => new AudioOutputNode();
    }
}
