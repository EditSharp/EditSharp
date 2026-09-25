using EditSharp.Audio.Engine;
using System;
using System.Collections.Generic;
using EditSharp.History;
using EditSharp.Editing;
 
namespace EditSharp.Components.Nodes
{
    public abstract class Node
    {
        public Guid Id { get; } = Guid.NewGuid();

        //the graph this node was added to; set by Graph, never by a snapshot
        internal Graph? Graph { get; set; }

        /// <summary>The clip this node is in, through any composites around it.</summary>
        internal Clips.Clip? OwnerClip
        {
            get
            {
                for (Graph? graph = Graph; graph is not null; graph = graph.Composite?.Graph)
                    if (graph.Clip is { } clip) return clip;
                return null;
            }
        }
 
        //fixed set, declared by the concrete node type
        public abstract IReadOnlyList<NodePort> Ports { get; }
 
        //bypass — see Graph's class remarks
        bool _enabled = true;
        [Editable("Enabled", Order = -100)]
        public bool Enabled { get => _enabled; set => Transaction.Set(this, ref _enabled, value, static (o, v) => o._enabled = v); }
 
        /// <summary>
        /// Deep copy with a FRESH Id — a duplicated clip's graph must not
        /// share node identity with the original, or a Connection recorded
        /// against one graph could be mistaken for referencing a node in
        /// the other.
        /// </summary>
        public abstract Node Duplicate();

        /// <summary>
        /// Every keyframeable property this node owns, so a clip can reach
        /// all of its tracks at once — a head trim/extend shifts every
        /// keyframe in the graph (see Clip.OnHeadInPointShift). Empty by
        /// default; a node with Animatable properties overrides it and lists
        /// each one. An animated property left out here simply stops
        /// following the content when the clip's head moves.
        /// </summary>
        public virtual IEnumerable<IAnimatable> Animatables => [];

        /// <summary>This node's audio DSP for a playback or export session; null for nodes that don't process audio.</summary>
        internal virtual IAudioProcessor? CreateAudioProcessor(AudioSession session) => null;
    }
}
 