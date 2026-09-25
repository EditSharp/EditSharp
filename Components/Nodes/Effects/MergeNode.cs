using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Channels;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;
 
namespace EditSharp.Components.Nodes.Effects
{
    /// <summary>
    /// Branch/recombine — the canonical node-graph pattern (split into two
    /// branches, effect each differently, recombine) that justifies a graph
    /// over a flat stack in the first place. Also the canonical way to
    /// combine TWO InputNodes in one graph now that a graph can have more
    /// than one (e.g. two VideoSourceNodes composited together).
    /// </summary>
    public sealed class MergeNode : Node
    {
        //ChannelBlendMode, not the old ffmpeg-oriented BlendMode enum — see
        //ChannelBlendMode.cs's own remarks. The Skia-native compositor is
        //the live rendering path and MergeNode composites two image streams
        //the exact same way a Channel composites onto the ones beneath it,
        //so the two should speak the same blend-mode vocabulary.
        ChannelBlendMode _blendMode = ChannelBlendMode.SrcOver;
        [Editable("Blend mode")]
        public ChannelBlendMode BlendMode { get => _blendMode; set => Transaction.Set(this, ref _blendMode, value, static (o, v) => o._blendMode = v); }
        Animatable<float> _mix = new(1f);
        [Editable("Mix", Min = 0, Max = 1, Step = 0.01)]
        public Animatable<float> Mix { get => _mix; set => Transaction.Set(this, ref _mix, value, static (o, v) => o._mix = v); } // 0 = pure A, 1 = pure B
 
        private static readonly NodePort[] StaticPorts =
        [
            new("A", PortType.Image, PortDirection.Input),
            new("B", PortType.Image, PortDirection.Input),
 
            //optional Value modulation input — when connected (typically to
            //a ValueConstantNode or a MathNode chain — see
            //EditSharp.Components.Nodes.Math), MULTIPLIES against Mix's own
            //keyframed value rather than replacing it, so an author keeps
            //Mix's own curve and layers a procedurally-computed modulation
            //on top of it
            new("MixModulation", PortType.Value, PortDirection.Input, optional: true),
 
            new("Result", PortType.Image, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override IEnumerable<IAnimatable> Animatables => [Mix];

        public override Node Duplicate() => Transaction.Suppressed(() => new MergeNode
        {
            Enabled = Enabled,
            BlendMode = BlendMode,
            Mix = Mix.Duplicate(),
        });
    }
}
 