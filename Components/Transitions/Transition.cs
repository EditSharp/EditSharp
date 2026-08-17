using System;

namespace EditSharp.Components.Transitions
{
    /// <summary>
    /// One channel-level transition between two adjacent clips. Abstract,
    /// mirroring Effect's shape exactly: each transition kind is its own
    /// class carrying only the parameters it actually needs, rather than
    /// one class with every transition's fields (Color, Angle, and
    /// whatever future transitions need) all present whether relevant or
    /// not — the concrete problem this replaces: the previous design was
    /// already showing this bloat with just three kinds.
    ///
    /// Unlike the old design, there's no TransitionType enum anymore — the
    /// CLASS is the type, same as Effect has no separate "EffectType" enum.
    /// Only transitions with a real Skia implementation exist as classes
    /// (see FadeTransition.cs, FadeToColorTransition.cs, SlideTransition.cs).
    /// The ~44 old ffmpeg xfade names (WipeLeft, CircleOpen, Dissolve, etc.)
    /// that checklist item 8 left unported have NO class here at all — not
    /// a reserved-and-throwing stub, genuinely not representable until
    /// someone adds a class for one, same as adding a new Effect subclass.
    /// This is a real, larger compatibility break than earlier item-8 gaps:
    /// those left the enum member in place and threw at render time; this
    /// removes the ability to even CONSTRUCT one of the unported kinds.
    /// </summary>
    public abstract class Transition
    {
        //how long the transition should last
        public TimeSpan Duration { get; set; }

        //deep copy — mirrors Effect.Duplicate for the same reason: clip
        //fragments produced by a split must not share Transition instances
        public abstract Transition Duplicate();
    }
}
