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
        internal Clips.Clip? OwnerClip => Graph?.OwnerClip;

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

        /// <summary>Describes what the node does to audio in the frequency domain, for one frame.</summary>
        /// <remarks>
        /// The default passes the first input through, or writes silence for a node with no audio input. A node that
        /// changes audio overrides this so waveform displays show its effect without rendering samples. A node that
        /// carries state between frames (a compressor's envelope) keeps it in <paramref name="state"/>.
        /// </remarks>
        /// <param name="context">The frame's content time and length.</param>
        /// <param name="inputs">One frame per audio input port, in port order; an unwired input is silence.</param>
        /// <param name="output">The frame to write the node's output into.</param>
        /// <param name="state">Whatever the node kept from the previous frame; null on the first.</param>
        public virtual void DescribeSpectrum(in Audio.Analysis.SpectralContext context, ReadOnlySpan<Audio.Analysis.SpectralFrame> inputs, Audio.Analysis.SpectralFrame output, ref object? state)
        {
            if (inputs.Length > 0) output.CopyFrom(inputs[0]);
            else output.Clear();
        }
    }
}
