using System;
using System.Collections.Generic;
using EditSharp.Components;
 
namespace EditSharp.Components.Effects
{
    public enum MathOperation { Add, Subtract, Multiply, Divide, Min, Max }
 
    /// <summary>
    /// Wraps one keyframeable float as a Value-port output — the simplest
    /// way to originate a number for a MathNode (or for one of the optional
    /// Value modulation inputs a few signal nodes now expose — see
    /// GainNode/AudioMixNode/MergeNode's own remarks) to consume.
    ///
    /// Declares ONLY a Value port, so EffectGraph.AddNode treats it as
    /// domain-universal (see EffectGraph.InferDomain) — the exact same
    /// ValueConstantNode class works unmodified inside a VideoClip's
    /// Image-domain graph or an AudioClip's Audio-domain graph.
    /// </summary>
    public sealed class ValueConstantNode : EffectNode
    {
        public Animatable<float> Value { get; set; } = new(0f);
 
        private static readonly NodePort[] StaticPorts = [new("Value", PortType.Value, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override EffectNode Duplicate() => new ValueConstantNode { Enabled = Enabled, Value = Value.Duplicate() };
    }
 
    /// <summary>
    /// The "base node type for math functions that are universally
    /// applicable" — combines two Value inputs with a plain arithmetic
    /// operation. Domain-universal for the same reason ValueConstantNode
    /// is (only Value ports), so the SAME MathNode class places into either
    /// an Image-domain or an Audio-domain graph.
    ///
    /// On its own this only produces a number — it has no effect on any
    /// signal until it's wired into one of the small set of nodes that
    /// expose an optional Value modulation input (GainNode's "Modulation",
    /// AudioMixNode's "MixAModulation"/"MixBModulation", MergeNode's
    /// "MixModulation" — see each node's own remarks). Wire a
    /// ValueConstantNode (or a whole MathNode chain) into one of those, and
    /// it multiplies against that node's own keyframed value every time
    /// it's evaluated, rather than replacing it outright — so the node's
    /// own curve and a procedurally-computed modulation compose, instead of
    /// one silently overriding the other.
    /// </summary>
    public sealed class MathNode : EffectNode
    {
        public MathOperation Operation { get; set; } = MathOperation.Add;
 
        private static readonly NodePort[] StaticPorts =
        [
            new("A", PortType.Value, PortDirection.Input),
            new("B", PortType.Value, PortDirection.Input),
            new("Result", PortType.Value, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override EffectNode Duplicate() => new MathNode { Enabled = Enabled, Operation = Operation };
    }
}
 