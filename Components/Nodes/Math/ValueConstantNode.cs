using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Nodes;
 
namespace EditSharp.Components.Nodes.Math
{
    /// <summary>
    /// Wraps one keyframeable float as a Value-port output — the simplest
    /// way to originate a number for a MathNode (or for one of the optional
    /// Value modulation inputs a few signal nodes now expose — see
    /// GainNode/AudioMixNode/MergeNode's own remarks) to consume.
    ///
    /// Declares ONLY a Value port, so Graph.AddNode treats it as
    /// domain-universal (see Graph.InferDomain) — the exact same
    /// ValueConstantNode class works unmodified inside a VideoClip's
    /// Image-domain graph or an AudioClip's Audio-domain graph.
    /// </summary>
    public sealed class ValueConstantNode : Node
    {
        public Animatable<float> Value { get; set; } = new(0f);
 
        private static readonly NodePort[] StaticPorts = [new("Value", PortType.Value, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override Node Duplicate() => new ValueConstantNode { Enabled = Enabled, Value = Value.Duplicate() };
    }
}
 