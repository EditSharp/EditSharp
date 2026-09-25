using EditSharp.Audio.Processors;
using EditSharp.Audio.Engine;
using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Nodes.Effects
{
    /// <summary>Adds two audio signals, each at its own level.</summary>
    /// <remarks>
    /// Inputs: A and B, plus optional Value inputs MixAModulation and MixBModulation
    /// that multiply <see cref="MixA"/> and <see cref="MixB"/>. Output: Result. Use it
    /// to combine two sources in one clip, or for parallel processing, such as
    /// mixing a heavily compressed copy under the dry signal.
    /// </remarks>
    [NodeKind("audio-mix", DisplayName = "Audio mix")]
    public sealed class AudioMixNode : Node
    {
        Animatable<float> _mixA = new(1f);
        /// <summary>The level of A, from 0 (silent) to 1 (unchanged).</summary>
        [Editable("Mix A", Min = 0, Max = 1, Step = 0.01)]
        public Animatable<float> MixA { get => _mixA; set => Transaction.Set(this, ref _mixA, value, static (o, v) => o._mixA = v); }
        Animatable<float> _mixB = new(1f);
        /// <summary>The level of B, from 0 (silent) to 1 (unchanged).</summary>
        [Editable("Mix B", Min = 0, Max = 1, Step = 0.01)]
        public Animatable<float> MixB { get => _mixB; set => Transaction.Set(this, ref _mixB, value, static (o, v) => o._mixB = v); }

        private static readonly NodePort[] StaticPorts =
        [
            new("A", PortType.Audio, PortDirection.Input),
            new("B", PortType.Audio, PortDirection.Input),

            new("MixAModulation", PortType.Value, PortDirection.Input, optional: true),
            new("MixBModulation", PortType.Value, PortDirection.Input, optional: true),

            new("Result", PortType.Audio, PortDirection.Output),
        ];

        /// <inheritdoc/>
        public override IReadOnlyList<NodePort> Ports => StaticPorts;

        /// <inheritdoc/>
        public override IEnumerable<IAnimatable> Animatables => [MixA, MixB];
        internal override IAudioProcessor CreateAudioProcessor(AudioSession session) => new AudioMixProcessor(this);

        /// <inheritdoc/>
        public override Node Duplicate() => Transaction.Suppressed(() => new AudioMixNode
        {
            Enabled = Enabled,
            MixA = MixA.Duplicate(),
            MixB = MixB.Duplicate(),
        });
    }
}
