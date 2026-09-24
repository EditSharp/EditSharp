using System;
using System.Collections.Generic;
using EditSharp.Components.Sources.Video;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;
 
namespace EditSharp.Components.Nodes.Sources
{
    /// <summary>
    /// Feeds a VideoSource's frames into the graph; a media file, a still
    /// image, or any other video kind; the node neither knows nor cares
    /// which. Replaces the old VideoClip.Source property directly.
    /// </summary>
    public sealed class VideoSourceNode : InputNode, ITrimmableInput
    {
        VideoSource _source = null!;
        [Editable("Source")]
        public required VideoSource Source { get => _source; set => Transaction.Set(this, ref _source, value, static (o, v) => o._source = v); }
 
        private static readonly NodePort[] StaticPorts = [new("Image", PortType.Image, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override Node Duplicate() => Transaction.Suppressed(() => new VideoSourceNode { Enabled = Enabled, Source = Source.Duplicate() });
 
        public TimeSpan InPoint
        {
            get => Source.Start ?? TimeSpan.Zero;
            set => Source.Start = value;
        }
 
        public TimeSpan MaxHeadroom => Source.Start ?? TimeSpan.Zero;
    }
}
 