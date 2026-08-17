using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Clips;
using EditSharp.Components.Transitions;

namespace EditSharp.Render
{
    /// <summary>
    /// Item 13: this file now holds ONLY the per-frame data model
    /// (FrameClip/FrameState/FrameChannel) — everything else that used to
    /// live here is gone, not just modified:
    ///
    ///   - IFrameFilterChainBuilder / SoftwareFrameFilterChainBuilder
    ///     (Build/ComposeChannel/BuildClip/BuildContent/ApplyTransition/
    ///     Draw/Flatten) -> superseded by SkFrameCompositor.cs, which
    ///     drives an in-process SKCanvas instead of building ffmpeg filter
    ///     lines and handing them to a subprocess.
    ///   - GeneratorColourAt -> SkGeneratorClip.ColourAt (item 9).
    ///   - SKColorLike -> gone outright. It existed purely to format an
    ///     SKColor as ffmpeg's `color=0xRRGGBB@a` hex syntax; nothing
    ///     downstream speaks ffmpeg filter syntax for clip content anymore.
    ///
    /// There is no filter GRAPH on the video side at all anymore. A frame
    /// is composited directly against an in-process SKCanvas — ffmpeg is
    /// only still involved for source video DECODE (SkSourceDecoder, one
    /// long-lived subprocess per active video clip) and the final mux/
    /// encode (FrameRenderer.FinalizeOutputAsync).
    /// </summary>
    internal sealed class FrameClip
    {
        public required Clip Clip { get; init; }
        public required ClipTransform Transform { get; init; }

        /// <summary>
        /// The clip's own native pixel size — a video/image source's real
        /// dimensions, a TextClip's rasterized block size, or 0x0 for
        /// GeneratorClip/NoiseClip (SkFrameCompositor.DrawClip substitutes
        /// canvas size for those — see its own remarks for why that's
        /// correct despite neither actually holding canvas-sized content).
        /// Used only for aspect-fit math (TransformExpressions.BaseFitSize/
        /// ComputeContentSize) — the actual pixel content is fetched
        /// separately, from SkClipContentSource, not carried on this class.
        /// </summary>
        public int NativeWidth { get; init; }
        public int NativeHeight { get; init; }

        /// <summary>
        /// Clip-relative seconds at this output frame — what a generator's
        /// colour ramp and a noise field's time coordinate are functions
        /// of. A video clip's decoder doesn't need this: SkSourceDecoder
        /// tracks its own position by call order, not by this value (see
        /// its own remarks on the "sequentially forward" contract).
        /// </summary>
        public double ClipSeconds { get; init; }
    }

    /// <summary>
    /// One frame's complete composite state: every clip visible on this
    /// frame, bottom channel first, each already resolved to its literal
    /// transform.
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
        /// The real Transition object (item 8's abstract-class hierarchy),
        /// not an enum member. Null both when no transition is mid-flight
        /// AND — per SkTransitionCompositor.Compose's own documented
        /// fallback — when Clips.Count == 2 but the channel has no
        /// Transition configured for this pair, which renders as a plain
        /// crossfade rather than failing.
        /// </summary>
        public Transition? Transition { get; init; }

        /// <summary>0 at the transition's first frame, 1 at its last.</summary>
        public double TransitionProgress { get; init; }
    }
}
