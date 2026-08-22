using System;
 
namespace EditSharp.Components.Clips
{
    /// <summary>
    /// Mirrors Source's own shape exactly (Start/Duration, both nullable) —
    /// see the schema doc for why this lives in its own wrapper rather than
    /// directly on TimelineVideoClip/TimelineAudioClip (Clip.Start already
    /// means something different) or baked into Timeline itself (a
    /// Timeline may be embedded more than once, at different in-points, and
    /// needs to stay reusable across embeddings).
    /// </summary>
    public sealed class TimelineReference
    {
        public required Timeline Timeline { get; set; }
 
        //in-point within the nested timeline; null = from the beginning
        public TimeSpan? Start { get; set; }
 
        //how much of the nested timeline to use from Start; null = to its natural end
        public TimeSpan? Duration { get; set; }
 
        public TimelineReference Duplicate() => new() { Timeline = Timeline, Start = Start, Duration = Duration };
    }
}
 