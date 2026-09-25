using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Channels;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Nodes.Effects
{
    /// <summary>Draws one image over another.</summary>
    /// <remarks>
    /// Inputs: A and B, plus an optional Value input, MixModulation, that multiplies
    /// <see cref="Mix"/>. Output: Result, the size of the larger image. Use it to
    /// combine two sources in one clip, or to recombine two branches of a graph
    /// that were affected differently.
    /// </remarks>
    public sealed class MergeNode : Node
    {
        ChannelBlendMode _blendMode = ChannelBlendMode.SrcOver;
        /// <summary>How B combines with A; the same modes channels use.</summary>
        [Editable("Blend mode")]
        public ChannelBlendMode BlendMode { get => _blendMode; set => Transaction.Set(this, ref _blendMode, value, static (o, v) => o._blendMode = v); }
        Animatable<float> _mix = new(1f);
        /// <summary>B's opacity over A, from 0 (only A) to 1 (B fully over A).</summary>
        [Editable("Mix", Min = 0, Max = 1, Step = 0.01)]
        public Animatable<float> Mix { get => _mix; set => Transaction.Set(this, ref _mix, value, static (o, v) => o._mix = v); }

        private static readonly NodePort[] StaticPorts =
        [
            new("A", PortType.Image, PortDirection.Input),
            new("B", PortType.Image, PortDirection.Input),

            new("MixModulation", PortType.Value, PortDirection.Input, optional: true),

            new("Result", PortType.Image, PortDirection.Output),
        ];

        /// <inheritdoc/>
        public override IReadOnlyList<NodePort> Ports => StaticPorts;

        /// <inheritdoc/>
        public override IEnumerable<IAnimatable> Animatables => [Mix];

        /// <inheritdoc/>
        public override Node Duplicate() => Transaction.Suppressed(() => new MergeNode
        {
            Enabled = Enabled,
            BlendMode = BlendMode,
            Mix = Mix.Duplicate(),
        });
    }
}
