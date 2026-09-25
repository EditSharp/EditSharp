using EditSharp.Audio.Engine;
using System;
using System.Collections.Generic;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Nodes
{
    /// <summary>One step in a clip's <see cref="Graph"/>: a source, an effect, a mask or a value.</summary>
    /// <remarks>A node's ports are fixed by its type. Its settings are its [Editable] properties.</remarks>
    public abstract class Node
    {
        /// <summary>Identifies the node within its graph; connections refer to nodes by it.</summary>
        public Guid Id { get; } = Guid.NewGuid();

        //the graph this node was added to; set by Graph, never by a snapshot
        internal Graph? Graph { get; set; }

        //the clip this node is in, through any composites around it
        internal Clips.Clip? OwnerClip
        {
            get
            {
                for (Graph? graph = Graph; graph is not null; graph = graph.Composite?.Graph)
                    if (graph.Clip is { } clip) return clip;
                return null;
            }
        }

        /// <summary>The node's ports, fixed by its type.</summary>
        public abstract IReadOnlyList<NodePort> Ports { get; }

        bool _enabled = true;
        /// <summary>Whether the node runs.</summary>
        /// <remarks>A disabled source outputs nothing (transparent, or silence). A disabled effect passes its first input through. A disabled mask node outputs no mask, so whatever it fed is unmasked, and a disabled Value node feeds nothing, so the node it fed uses its own value.</remarks>
        [Editable("Enabled", Order = -100)]
        public bool Enabled { get => _enabled; set => Transaction.Set(this, ref _enabled, value, static (o, v) => o._enabled = v); }

        /// <summary>A deep copy with a new <see cref="Id"/>, so connections in the two graphs can't be confused.</summary>
        /// <remarks>History is suppressed: the copy has nothing to undo.</remarks>
        /// <returns>The copy.</returns>
        public abstract Node Duplicate();

        /// <summary>Every keyframeable value the node owns.</summary>
        /// <remarks>Trimming a clip's head shifts every keyframe these hold, so the animation stays with the content. A node with Animatable properties lists every one of them here.</remarks>
        public virtual IEnumerable<IAnimatable> Animatables => [];

        //this node's audio processing for one session; null for nodes that don't process audio
        internal virtual IAudioProcessor? CreateAudioProcessor(AudioSession session) => null;
    }
}
