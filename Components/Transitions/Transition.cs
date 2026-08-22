using System;
using EditSharp.Components.Clips;
 
namespace EditSharp.Components.Transitions
{
    /// <summary>
    /// One channel-level transition between two adjacent clips. Abstract —
    /// the CLASS is the type, no separate TransitionType enum, matching
    /// Effect/EffectNode's own "the class is the type" convention elsewhere
    /// in this schema.
    ///
    /// NEW in this rewrite: carries its own From/To directly (previously a
    /// Channel held a separate (Clip, Transition) tuple list) — see the
    /// schema doc's Channel section. This is what lets Channel.Transitions
    /// simply be a List&lt;Transition&gt;.
    /// </summary>
    public abstract class Transition
    {
        public Clip From { get; internal set; } = null!;
        public Clip To { get; internal set; } = null!;
 
        //the transition's length — see Channel's overlap invariant for the
        //carve-out this creates and how it's actually achieved (extending
        //into each clip's own trim-handle material, not destructive trimming)
        public TimeSpan Duration { get; set; }
 
        //deep copy — clip fragments produced by a split must not share
        //Transition instances. From/To are NOT copied here — see
        //Channel.SplitClip, which drops any Transition referencing a clip
        //that no longer exists rather than trying to re-point it
        public abstract Transition Duplicate();
    }
}
 