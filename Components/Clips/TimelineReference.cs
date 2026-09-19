using System;
using EditSharp.History;
using EditSharp.Editing;
 
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
        Timeline _timeline = null!;
        [Editable("Timeline")]
        public required Timeline Timeline { get => _timeline; set => Transaction.Set(this, ref _timeline, value, static (o, v) => o._timeline = v); }
 
        //in-point within the nested timeline; null = from the beginning
        TimeSpan? _start;
        [Editable("In point")]
        public TimeSpan? Start { get => _start; set => Transaction.Set(this, ref _start, value, static (o, v) => o._start = v); }
 
        //how much of the nested timeline to use from Start; null = to its natural end
        TimeSpan? _duration;
        [Editable("Duration")]
        public TimeSpan? Duration { get => _duration; set => Transaction.Set(this, ref _duration, value, static (o, v) => o._duration = v); }
 
        public TimelineReference Duplicate() => Transaction.Suppressed(() => new TimelineReference { Timeline = Timeline, Start = Start, Duration = Duration });
    }
}
 