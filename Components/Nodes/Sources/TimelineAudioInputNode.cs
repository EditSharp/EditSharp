using EditSharp.Audio.Processors;
using EditSharp.Audio.Engine;
using System;
using System.Collections.Generic;
using EditSharp.Components.Clips;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;
 
namespace EditSharp.Components.Nodes.Sources
{
    /// <summary>
    /// Embeds another Timeline's fully mixed audio. Replaces the old
    /// TimelineAudioClip Clip subtype — TimelineReference itself is
    /// unchanged, it just lives on a node now.
    /// </summary>
    public sealed class TimelineAudioInputNode : InputNode, ITrimmableInput
    {
        TimelineReference _reference = null!;
        [Editable("Timeline")]
        public required TimelineReference Reference { get => _reference; set => Transaction.Set(this, ref _reference, value, static (o, v) => o._reference = v); }
 
        private static readonly NodePort[] StaticPorts = [new("Audio", PortType.Audio, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        internal override IAudioProcessor CreateAudioProcessor(AudioSession session) =>
            new ContentInputProcessor(() => Reference, () => new NestedTimelineContentAudio(Reference, session), session);
        public override Node Duplicate() => Transaction.Suppressed(() => new TimelineAudioInputNode { Enabled = Enabled, Reference = Reference.Duplicate() });
 
        public TimeSpan InPoint
        {
            get => Reference.Start ?? TimeSpan.Zero;
            set => Reference.Start = value;
        }
 
        public TimeSpan MaxHeadroom => Reference.Start ?? TimeSpan.Zero;
    }
}
 