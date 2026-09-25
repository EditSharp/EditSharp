using EditSharp.Audio.Processors;
using EditSharp.Audio.Engine;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Nodes.Effects
{
    /// <summary>One band of an <see cref="EQNode"/>: a peaking filter that boosts or cuts around a frequency.</summary>
    public sealed class EQBand
    {
        Animatable<float> _frequencyHz = new(1000f);
        /// <summary>The centre frequency, in hertz.</summary>
        [Editable("Frequency", Min = 20, Max = 20000, Step = 1, Unit = "Hz")]
        public Animatable<float> FrequencyHz { get => _frequencyHz; set => Transaction.Set(this, ref _frequencyHz, value, static (o, v) => o._frequencyHz = v); }
        Animatable<float> _gainDb = new(0f);
        /// <summary>How much to boost (positive) or cut (negative) at the centre frequency, in dB.</summary>
        [Editable("Gain", Min = -24, Max = 24, Step = 0.5, Unit = "dB")]
        public Animatable<float> GainDb { get => _gainDb; set => Transaction.Set(this, ref _gainDb, value, static (o, v) => o._gainDb = v); }
        Animatable<float> _q = new(1f);
        /// <summary>How narrow the band is; higher affects a narrower range of frequencies.</summary>
        [Editable("Q", Min = 0.1, Max = 10, Step = 0.1)]
        public Animatable<float> Q { get => _q; set => Transaction.Set(this, ref _q, value, static (o, v) => o._q = v); }

        /// <summary>A deep copy of the band.</summary>
        /// <returns>The copy.</returns>
        public EQBand Duplicate() => Transaction.Suppressed(() => new EQBand
        {
            FrequencyHz = FrequencyHz.Duplicate(),
            GainDb = GainDb.Duplicate(),
            Q = Q.Duplicate(),
        });
    }

    /// <summary>An equalizer: peaking bands applied one after another.</summary>
    /// <remarks>Input: Audio. Output: Audio. With no bands it passes the audio through unchanged.</remarks>
    [NodeKind("eq", DisplayName = "EQ")]
    public sealed class EQNode : Node
    {
        List<EQBand> _bands = [];
        /// <summary>The bands, applied in order.</summary>
        [Editable("Bands")]
        public List<EQBand> Bands { get => _bands; set => Transaction.Set(this, ref _bands, value, static (o, v) => o._bands = v); }

        private static readonly NodePort[] StaticPorts =
        [
            new("Audio", PortType.Audio, PortDirection.Input),
            new("Audio", PortType.Audio, PortDirection.Output),
        ];

        /// <inheritdoc/>
        public override IReadOnlyList<NodePort> Ports => StaticPorts;

        /// <inheritdoc/>
        public override IEnumerable<IAnimatable> Animatables => Bands.SelectMany(b => new IAnimatable[] { b.FrequencyHz, b.GainDb, b.Q });
        internal override IAudioProcessor CreateAudioProcessor(AudioSession session) => new EqProcessor(this);

        /// <inheritdoc/>
        public override Node Duplicate() => Transaction.Suppressed(() => new EQNode
        {
            Enabled = Enabled,
            Bands = [.. Bands.ConvertAll(b => b.Duplicate())],
        });
    }
}
