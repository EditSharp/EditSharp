using System;
using System.Collections.Generic;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;
 
namespace EditSharp.Components.Nodes.Sources
{
    /// <summary>Replaces the old NoiseClip. No in-point concept — procedural, generates for however long it's asked.</summary>
    public sealed class NoiseInputNode : InputNode
    {
        int _seed = Random.Shared.Next();
        [Editable("Seed")]
        public int Seed { get => _seed; set => Transaction.Set(this, ref _seed, value, static (o, v) => o._seed = v); }
        float _detail = 0.03f;
        [Editable("Detail", Min = 0, Max = 1, Step = 0.001)]
        public float Detail { get => _detail; set => Transaction.Set(this, ref _detail, value, static (o, v) => o._detail = v); }
        float _seetheRate = 0.03f;
        [Editable("Seethe rate", Min = 0, Max = 1, Step = 0.001)]
        public float SeetheRate { get => _seetheRate; set => Transaction.Set(this, ref _seetheRate, value, static (o, v) => o._seetheRate = v); }
 
        private static readonly NodePort[] StaticPorts = [new("Image", PortType.Image, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override Node Duplicate() => Transaction.Suppressed(() => new NoiseInputNode
        {
            Enabled = Enabled,
            Seed = Seed,
            Detail = Detail,
            SeetheRate = SeetheRate,
        });
    }
}
 