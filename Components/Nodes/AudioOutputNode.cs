using System.Collections.Generic;

namespace EditSharp.Components.Nodes
{
    /// <summary>The output of an audio clip's graph: the sound the clip plays.</summary>
    /// <remarks>Input: Audio.</remarks>
    [NodeKind("audio-output", DisplayName = "Audio output", Listed = false)]
    public sealed class AudioOutputNode : OutputNode
    {
        private static readonly NodePort[] StaticPorts = [new("Audio", PortType.Audio, PortDirection.Input)];
        /// <inheritdoc/>
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        /// <inheritdoc/>
        public override Node Duplicate() => new AudioOutputNode();
    }
}
