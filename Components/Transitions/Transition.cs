using System;
using EditSharp.Components.Clips;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Transitions
{
    /// <summary>A change from one clip to the next on a channel, over the time the two overlap.</summary>
    /// <remarks>Attach one with <see cref="Channels.Channel.AddTransition"/>. It's removed when an edit leaves its clips no longer overlapping by its duration.</remarks>
    public abstract class Transition
    {
        Clip _from = null!;
        /// <summary>The clip the transition leaves; set by <see cref="Channels.Channel.AddTransition"/>.</summary>
        public Clip From { get => _from; internal set => Transaction.Set(this, ref _from, value, static (o, v) => o._from = v); }
        Clip _to = null!;
        /// <summary>The clip the transition arrives at; set by <see cref="Channels.Channel.AddTransition"/>.</summary>
        public Clip To { get => _to; internal set => Transaction.Set(this, ref _to, value, static (o, v) => o._to = v); }

        TimeSpan _duration;
        /// <summary>How long the transition lasts: the time its two clips overlap.</summary>
        [Editable("Duration")]
        public TimeSpan Duration { get => _duration; set => Transaction.Set(this, ref _duration, value, static (o, v) => o._duration = v); }

        /// <summary>A copy with the same settings, not attached to any clips.</summary>
        /// <remarks>Nothing is recorded in history.</remarks>
        /// <returns>The copy.</returns>
        public abstract Transition Duplicate();
    }
}
