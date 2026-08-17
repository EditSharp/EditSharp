namespace EditSharp.Components
{
    /// <summary>
    /// Replaces the old ffmpeg-era `BlendMode` enum (deleted). Rather than
    /// exposing `SKBlendMode` directly on `Channel.BlendMode`, this mirrors
    /// every native Skia blend mode 1:1 by name AND reserves four extra
    /// slots for the old enum's arithmetic modes that have no SKBlendMode
    /// equivalent at all (Average, Negation, Divide, Subtract — Photoshop/
    /// GIMP-era formulas, not Porter-Duff/CSS compositing operators).
    ///
    /// Why a wrapper again, after item 7 deliberately deleted the old
    /// wrapper in favour of raw SKBlendMode: those four modes need
    /// SOMEWHERE to live if they're ever implemented via a custom
    /// SKRuntimeEffect (SkSL) shader, and `SKBlendMode` is a sealed native
    /// enum — there's no way to add a value to it. A superset wrapper gives
    /// them a stable, permanent slot in the public API now, so wiring in a
    /// real implementation later is additive (SkChannelCompositor's mapping
    /// gains a case) rather than another breaking enum swap on Channel.
    ///
    /// The four reserved slots THROW today (see SkChannelCompositor's
    /// mapping) rather than silently doing nothing or falling back to
    /// SrcOver — a clip configured with one of these gets a clear error,
    /// not a silently-wrong render.
    /// </summary>
    public enum ChannelBlendMode
    {
        // Mirrors SKBlendMode exactly, by name — every native Skia blend
        // mode is available. Xor is included here as a normal, directly-
        // mapped mode: unlike the old ffmpeg `BlendMode.Xor` (bitwise
        // per-channel XOR), this one was never actually wired to any
        // shipped behaviour before this rewrite, so there's no prior
        // expectation to preserve or contradict — SKBlendMode.Xor's own
        // (Porter-Duff alpha-coverage) semantics are simply what "Xor"
        // means going forward.
        Clear, Src, Dst, SrcOver, DstOver, SrcIn, DstIn, SrcOut, DstOut,
        SrcATop, DstATop, Xor, Plus, Modulate, Screen, Overlay, Darken,
        Lighten, ColorDodge, ColorBurn, HardLight, SoftLight, Difference,
        Exclusion, Multiply, Hue, Saturation, Color, Luminosity,

        // Reserved — no native SKBlendMode equivalent. Throws in
        // SkChannelCompositor until/unless a custom SkSL SKRuntimeEffect
        // shader is built for one of these (per the conversation: only
        // build it if an actual need shows up, not preemptively).
        Average, Negation, Divide, Subtract,
    }
}
