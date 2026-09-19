using System;
using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;
 
namespace EditSharp.Components.Nodes.Sources
{
    /// <summary>A real audio (or video-with-audio) file. Replaces the old AudioClip.Source property directly.</summary>
    public sealed class AudioSourceNode : InputNode, ITrimmableInput
    {
        Source _source = null!;
        [Editable("Source")]
        public required Source Source { get => _source; set => Transaction.Set(this, ref _source, value, static (o, v) => o._source = v); }
 
        private static readonly NodePort[] StaticPorts = [new("Audio", PortType.Audio, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override Node Duplicate() => Transaction.Suppressed(() => new AudioSourceNode { Enabled = Enabled, Source = Source.Duplicate() });
 
        public TimeSpan InPoint
        {
            get => Source.Start ?? TimeSpan.Zero;
            set => Source.Start = value;
        }
 
        public TimeSpan MaxHeadroom => Source.Start ?? TimeSpan.Zero;
    }
}
 