using System;
using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Nodes;
 
namespace EditSharp.Components.Nodes.Sources.Video
{
    /// <summary>
    /// A real media file, or a still image held for the clip's duration
    /// (an image is a Source with SourceType.Image, same convention as
    /// before this rewrite — no separate "ImageClip" node type). Replaces
    /// the old VideoClip.Source property directly.
    /// </summary>
    public sealed class MediaSourceNode : InputNode, ITrimmableInput
    {
        public required Source Source { get; set; }
 
        private static readonly NodePort[] StaticPorts = [new("Image", PortType.Image, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override Node Duplicate() => new MediaSourceNode { Enabled = Enabled, Source = Source.Duplicate() };
 
        public TimeSpan InPoint
        {
            get => Source.Start ?? TimeSpan.Zero;
            set => Source.Start = value;
        }
 
        public TimeSpan MaxHeadroom => Source.Start ?? TimeSpan.Zero;
    }
}
 