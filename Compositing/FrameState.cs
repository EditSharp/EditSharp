using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Channels;
using EditSharp.Components.Clips;
using EditSharp.Components.Transitions;
 
namespace EditSharp.Compositing
{
    /// <summary>
    /// This file holds ONLY the per-frame data model (FrameClip/FrameState/
    /// FrameChannel) — see git history / prior migration notes for what used
    /// to live here (ffmpeg filter-line building, all superseded by
    /// FrameCompositor.cs).
    ///
    /// "CLIPS ARE GRAPHS" REWRITE: FrameClip no longer carries Transform,
    /// NativeWidth, or NativeHeight. There is no longer one single "the
    /// clip's transform" — TransformNode carries its own ClipTransform data,
    /// and a graph can have more than one TransformNode. There is no longer
    /// one single "the clip's native size" either — a graph can have more
    /// than one InputNode (VideoSourceNode/TextInputNode/etc.), each with its
    /// OWN native size, and TransformNode now derives its native size
    /// directly from whatever image is actually upstream of it at evaluation
    /// time (see ImageGraphEvaluator's own remarks) rather than from a
    /// value precomputed here. FrameClip is therefore reduced to exactly what
    /// ClipContentSource/ClipCompositor actually need externally:
    /// which clip, and what instant of it.
    /// </summary>
    internal sealed class FrameClip
    {
        public required Clip Clip { get; init; }
 
        /// <summary>
        /// Clip-relative seconds at this output frame — what every effect
        /// node's Animatable fields (TintNode.Color, TransformNode.Transform,
        /// a generator/noise InputNode's own time-varying fields) are
        /// evaluated at. A video clip's decoder doesn't need this: SourceDecoder
        /// tracks its own position by call order, not by this value.
        /// </summary>
        public double ClipSeconds { get; init; }
    }
 
    /// <summary>
    /// One frame's complete composite state: every clip visible on this
    /// frame, bottom channel first. Built only from VideoChannels — see
    /// FrameStateResolver; AudioChannel contributes nothing to a video frame.
    /// </summary>
    internal sealed class FrameState
    {
        public required int FrameIndex { get; init; }
        public required List<FrameChannel> Channels { get; init; }
    }
 
    internal sealed class FrameChannel
    {
        public required ChannelBlendMode BlendMode { get; init; }
 
        /// <summary>
        /// Clips drawn on this channel this frame. Normally one — a
        /// channel's clips cannot overlap — but exactly two while a
        /// transition between adjacent clips is mid-flight, in which case
        /// TransitionProgress says how far through it is.
        /// </summary>
        public required List<FrameClip> Clips { get; init; }
 
        /// <summary>
        /// The real Transition object. Null both when no transition is
        /// mid-flight AND — per TransitionCompositor.Compose's own
        /// documented fallback — when Clips.Count == 2 but the channel has no
        /// Transition configured for this pair, which renders as a plain
        /// crossfade rather than failing.
        /// </summary>
        public Transition? Transition { get; init; }
 
        /// <summary>0 at the transition's first frame, 1 at its last.</summary>
        public double TransitionProgress { get; init; }
    }
}
 