using System;
using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Nodes;
 
namespace EditSharp.Components.Nodes.Sources.Audio
{
    /// <summary>A real audio (or video-with-audio) file. Replaces the old AudioClip.Source property directly.</summary>
    public sealed class AudioSourceNode : InputNode, ITrimmableInput
    {
        public required Source Source { get; set; }
 
        private static readonly NodePort[] StaticPorts = [new("Audio", PortType.Audio, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override Node Duplicate() => new AudioSourceNode { Enabled = Enabled, Source = Source.Duplicate() };
 
        public TimeSpan InPoint
        {
            get => Source.Start ?? TimeSpan.Zero;
            set => Source.Start = value;
        }
 
        public TimeSpan MaxHeadroom => Source.Start ?? TimeSpan.Zero;
    }
}
 