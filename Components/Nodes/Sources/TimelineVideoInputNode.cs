using System;
using System.Collections.Generic;
using EditSharp.Components.Clips;
using EditSharp.Components.Nodes;
 
namespace EditSharp.Components.Nodes.Sources
{
    /// <summary>
    /// Embeds another Timeline's fully composited picture. Replaces the old
    /// TimelineVideoClip Clip subtype — TimelineReference itself is
    /// unchanged, it just lives on a node now instead of directly on a
    /// dedicated Clip subtype.
    /// </summary>
    public sealed class TimelineVideoInputNode : InputNode, ITrimmableInput
    {
        public required TimelineReference Reference { get; set; }
 
        private static readonly NodePort[] StaticPorts = [new("Image", PortType.Image, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override Node Duplicate() => new TimelineVideoInputNode { Enabled = Enabled, Reference = Reference.Duplicate() };
 
        public TimeSpan InPoint
        {
            get => Reference.Start ?? TimeSpan.Zero;
            set => Reference.Start = value;
        }
 
        public TimeSpan MaxHeadroom => Reference.Start ?? TimeSpan.Zero;
    }
}
 