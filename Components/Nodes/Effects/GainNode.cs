using EditSharp.Audio.Processors;
using EditSharp.Audio.Engine;
using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;
 
namespace EditSharp.Components.Nodes.Effects
{
    /// <summary>
    /// Auto-created and wired in by default (see Graph.CreateAudioGraph)
    /// — this is what replaces the old flat AudioClip.Volume field. A
    /// normal, removable node beyond that default: an artist can delete it,
    /// add several gain stages at different points in a chain, or route
    /// around it entirely.
    /// </summary>
    public sealed class GainNode : Node
    {
        Animatable<float> _gain = new(1f);
        [Editable("Gain", Min = 0, Max = 4, Step = 0.01)]
        public Animatable<float> Gain { get => _gain; set => Transaction.Set(this, ref _gain, value, static (o, v) => o._gain = v); }
 
        private static readonly NodePort[] StaticPorts =
        [
            new("Audio", PortType.Audio, PortDirection.Input),
 
            //optional Value modulation input (see
            //EditSharp.Components.Nodes.Math's own remarks) — when
            //connected, MULTIPLIES against Gain's own keyframed value
            //rather than replacing it
            new("Modulation", PortType.Value, PortDirection.Input, optional: true),
 
            new("Audio", PortType.Audio, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override IEnumerable<IAnimatable> Animatables => [Gain];
        internal override IAudioProcessor CreateAudioProcessor(AudioSession session) => new GainProcessor(this);

        public override Node Duplicate() => Transaction.Suppressed(() => new GainNode { Enabled = Enabled, Gain = Gain.Duplicate() });
    }
}
 