using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Channels;
using EditSharp.Components.Clips;
using EditSharp.Components.Nodes;
using EditSharp.Components.Transitions;

namespace EditSharp.Compositing
{
    //one clip in one frame: which clip, its graph snapshot, and which instant of it
    internal sealed class FrameClip
    {
        public required Clip Clip { get; init; }

        /// <summary>The clip's graph as it was when the frame was resolved; compose from this, not Clip.Graph.</summary>
        public required Graph Graph { get; init; }

        //the clip's content time at this frame, which keyframed values are evaluated at
        public Time ContentTime { get; init; }
    }

    /// <summary>
    /// One frame's complete composite state: every clip visible on this
    /// frame, bottom channel first, from the video channels only.
    /// </summary>
    internal sealed class FrameState
    {
        public required int FrameIndex { get; init; }
        public required List<FrameChannel> Channels { get; init; }
    }

    internal sealed class FrameChannel
    {
        public required ChannelBlendMode BlendMode { get; init; }

        //one clip, or two while a transition between neighbours is under way
        public required List<FrameClip> Clips { get; init; }

        //the transition under way; null when there's none, or when two clips meet with none set (a plain crossfade)
        public Transition? Transition { get; init; }

        /// <summary>0 at the transition's first frame, 1 at its last.</summary>
        public double TransitionProgress { get; init; }
    }
}
