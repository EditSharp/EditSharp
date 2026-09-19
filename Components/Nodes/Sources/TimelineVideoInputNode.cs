using System;
using System.Collections.Generic;
using EditSharp.Components.Clips;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;
 
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
        TimelineReference _reference = null!;
        [Editable("Timeline")]
        public required TimelineReference Reference { get => _reference; set => Transaction.Set(this, ref _reference, value, static (o, v) => o._reference = v); }
 
        private static readonly NodePort[] StaticPorts = [new("Image", PortType.Image, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override Node Duplicate() => Transaction.Suppressed(() => new TimelineVideoInputNode { Enabled = Enabled, Reference = Reference.Duplicate() });
 
        public TimeSpan InPoint
        {
            get => Reference.Start ?? TimeSpan.Zero;
            set => Reference.Start = value;
        }
 
        public TimeSpan MaxHeadroom => Reference.Start ?? TimeSpan.Zero;
    }
}
 