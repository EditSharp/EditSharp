using System.Collections.Generic;
using EditSharp.Components.Clips;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;
 
namespace EditSharp.Components.Nodes.Effects
{
    /// <summary>
    /// Wraps a clip's transform. Auto-created and wired in by default when
    /// a VideoClip's graph is first constructed, but is otherwise a normal,
    /// removable, rewireable node — this is what replaces the old
    /// EffectStage.PreTransform/PostTransform enum entirely (see the schema
    /// doc): "stage" is purely a function of a node's position relative to
    /// TransformNode in the graph, not a fixed property of an effect type.
    ///
    /// REWRITE: this node now carries its OWN ClipTransform data (Position/
    /// Scale/Rotation/Pitch/Yaw), rather than being a data-less marker
    /// whose data lived on the owning VisualClip — there is no more
    /// VisualClip to hold it now that VideoClip is the only concrete
    /// visual Clip type and a clip IS its graph. A graph is free to have
    /// more than one TransformNode (e.g. one per branch before a MergeNode)
    /// now that Transform data travels with the node instead of the clip.
    /// </summary>
    public sealed class TransformNode : Node
    {
        ClipTransform _transform = new();
        [Editable("Transform")]
        public ClipTransform Transform { get => _transform; set => Transaction.Set(this, ref _transform, value, static (o, v) => o._transform = v); }
 
        private static readonly NodePort[] StaticPorts =
        [
            new("Image", PortType.Image, PortDirection.Input),
            new("Image", PortType.Image, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override IEnumerable<IAnimatable> Animatables => Transform.Animatables;

        public override Node Duplicate() => Transaction.Suppressed(() => new TransformNode { Enabled = Enabled, Transform = Transform.Duplicate() });
    }
}
 