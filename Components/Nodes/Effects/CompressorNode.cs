using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;
 
namespace EditSharp.Components.Nodes.Effects
{
    public sealed class CompressorNode : Node
    {
        Animatable<float> _threshold = new(-18f);
        [Editable("Threshold", Min = -60, Max = 0, Step = 0.5, Unit = "dB")]
        public Animatable<float> Threshold { get => _threshold; set => Transaction.Set(this, ref _threshold, value, static (o, v) => o._threshold = v); }
        Animatable<float> _ratio = new(4f);
        [Editable("Ratio", Min = 1, Max = 20, Step = 0.1)]
        public Animatable<float> Ratio { get => _ratio; set => Transaction.Set(this, ref _ratio, value, static (o, v) => o._ratio = v); }
        Animatable<float> _attackMs = new(10f);
        [Editable("Attack", Min = 0, Max = 500, Step = 1, Unit = "ms")]
        public Animatable<float> AttackMs { get => _attackMs; set => Transaction.Set(this, ref _attackMs, value, static (o, v) => o._attackMs = v); }
        Animatable<float> _releaseMs = new(100f);
        [Editable("Release", Min = 0, Max = 2000, Step = 1, Unit = "ms")]
        public Animatable<float> ReleaseMs { get => _releaseMs; set => Transaction.Set(this, ref _releaseMs, value, static (o, v) => o._releaseMs = v); }
        Animatable<float> _makeupGainDb = new(0f);
        [Editable("Makeup gain", Min = -24, Max = 24, Step = 0.5, Unit = "dB")]
        public Animatable<float> MakeupGainDb { get => _makeupGainDb; set => Transaction.Set(this, ref _makeupGainDb, value, static (o, v) => o._makeupGainDb = v); }
 
        private static readonly NodePort[] StaticPorts =
        [
            new("Audio", PortType.Audio, PortDirection.Input),
            new("Audio", PortType.Audio, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override IEnumerable<IAnimatable> Animatables => [Threshold, Ratio, AttackMs, ReleaseMs, MakeupGainDb];

        public override Node Duplicate() => Transaction.Suppressed(() => new CompressorNode
        {
            Enabled = Enabled,
            Threshold = Threshold.Duplicate(),
            Ratio = Ratio.Duplicate(),
            AttackMs = AttackMs.Duplicate(),
            ReleaseMs = ReleaseMs.Duplicate(),
            MakeupGainDb = MakeupGainDb.Duplicate(),
        });
    }
}
 