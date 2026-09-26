using EditSharp.Audio.Processors;
using EditSharp.Audio.Engine;
using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Nodes.Effects
{
    /// <summary>Changes the volume.</summary>
    /// <remarks>Input: Audio, plus an optional Value input, Modulation, that multiplies <see cref="Gain"/>. Output: Audio. A new audio clip's graph has one.</remarks>
    [NodeKind("gain", DisplayName = "Gain")]
    public sealed class GainNode : Node
    {
        Animatable<float> _gain = new(1f);
        /// <summary>The volume as a multiplier: 0 is silent, 1 unchanged, 2 twice as loud.</summary>
        [Editable("Gain", Min = 0, Max = 4, Step = 0.01)]
        public Animatable<float> Gain { get => _gain; set => Transaction.Set(this, ref _gain, value, static (o, v) => o._gain = v); }

        private static readonly NodePort[] StaticPorts =
        [
            new("Audio", PortType.Audio, PortDirection.Input),

            new("Modulation", PortType.Value, PortDirection.Input, optional: true),

            new("Audio", PortType.Audio, PortDirection.Output),
        ];

        /// <inheritdoc/>
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        /// <inheritdoc/>
        public override IEnumerable<IAnimatable> Animatables => [Gain];
        internal override IAudioProcessor CreateAudioProcessor(AudioSession session) => new GainProcessor(this);

        /// <inheritdoc/>
        public override Node Duplicate() => Transaction.Suppressed(() => new GainNode { Enabled = Enabled, Gain = Gain.Duplicate() });

        /// <inheritdoc/>
        public override void DescribeSpectrum(in Audio.Analysis.SpectralContext context, ReadOnlySpan<Audio.Analysis.SpectralFrame> inputs, Audio.Analysis.SpectralFrame output, ref object? state)
        {
            base.DescribeSpectrum(context, inputs, output, ref state);
            output.Scale(Gain.Evaluate(context.ContentTime));
        }
    }
}
