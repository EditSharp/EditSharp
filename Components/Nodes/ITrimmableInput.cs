using System;
 
namespace EditSharp.Components.Nodes
{
    /// <summary>
    /// A node whose own in-point should shift in lockstep with every OTHER
    /// trimmable input when the owning Clip's head is trimmed/extended —
    /// see Clip.OnHeadInPointShift/MaxHeadExtend, and the schema doc's
    /// multi-input-trim rule ("shift them all together, clamped by whichever
    /// one has the least room left").
    ///
    /// Implemented by VideoSourceNode/AudioSourceNode (wrapping
    /// Source.Start) and TimelineVideoInputNode/TimelineAudioInputNode
    /// (wrapping TimelineReference.Start). Generator/procedural/text input
    /// nodes (ColorGeneratorInputNode, NoiseInputNode, TextInputNode,
    /// ToneGeneratorInputNode) do NOT implement this — they have no
    /// "in-point" concept, matching the old TextClip/GeneratorClip/
    /// NoiseClip's inherited no-op/unbounded Clip defaults from before this
    /// rewrite.
    /// </summary>
    public interface ITrimmableInput
    {
        /// <summary>The node's own in-point. Setting this is what OnHeadInPointShift actually does, per node.</summary>
        TimeSpan InPoint { get; set; }
 
        /// <summary>How far InPoint could move EARLIER (an extend) — TimeSpan.MaxValue if unconstrained.</summary>
        TimeSpan MaxHeadroom { get; }

        /// <summary>How much content follows the in-point when there's a known hard end; null when unbounded, looping, or not known yet.</summary>
        TimeSpan? ContentLength { get; }
    }
}
 