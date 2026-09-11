using System;
using System.Collections.Generic;
using EditSharp.Components.Clips;
using EditSharp.Components.Nodes;
 
namespace EditSharp.Components.Nodes.Sources
{
    /// <summary>
    /// Embeds another Timeline's fully mixed audio. Replaces the old
    /// TimelineAudioClip Clip subtype — TimelineReference itself is
    /// unchanged, it just lives on a node now.
    /// </summary>
    public sealed class TimelineAudioInputNode : InputNode, ITrimmableInput
    {
        public required TimelineReference Reference { get; set; }
 
        private static readonly NodePort[] StaticPorts = [new("Audio", PortType.Audio, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override Node Duplicate() => new TimelineAudioInputNode { Enabled = Enabled, Reference = Reference.Duplicate() };
 
        public TimeSpan InPoint
        {
            get => Reference.Start ?? TimeSpan.Zero;
            set => Reference.Start = value;
        }
 
        public TimeSpan MaxHeadroom => Reference.Start ?? TimeSpan.Zero;
    }
}
 