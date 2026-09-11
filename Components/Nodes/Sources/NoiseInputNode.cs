using System;
using System.Collections.Generic;
using EditSharp.Components.Nodes;
 
namespace EditSharp.Components.Nodes.Sources
{
    /// <summary>Replaces the old NoiseClip. No in-point concept — procedural, generates for however long it's asked.</summary>
    public sealed class NoiseInputNode : InputNode
    {
        public int Seed { get; set; } = Random.Shared.Next();
        public float Detail { get; set; } = 0.03f;
        public float SeetheRate { get; set; } = 0.03f;
 
        private static readonly NodePort[] StaticPorts = [new("Image", PortType.Image, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override Node Duplicate() => new NoiseInputNode
        {
            Enabled = Enabled,
            Seed = Seed,
            Detail = Detail,
            SeetheRate = SeetheRate,
        };
    }
}
 