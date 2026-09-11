using System;
using System.Collections.Generic;
using SkiaSharp;
using EditSharp.Components;
using EditSharp.Components.Channels;
 
namespace EditSharp.Compositing
{
    /// <summary>
    /// Item 7: replaces FrameFilterChain.Draw and the whole EditSharp.Components
    /// `BlendMode` enum. `BlendMode` existed only because ffmpeg's `blend`
    /// filter ignores alpha entirely (it blends the whole frame, opaque or
    /// not) and had a narrower mode set than Skia's — both reasons are gone
    /// once compositing is native Skia:
    ///
    ///   - Skia's SKBlendMode operators are alpha-aware Porter-Duff/CSS
    ///     compositing formulas by construction. A channel's own transparency
    ///     is respected automatically as blend coverage — there is nothing
    ///     equivalent needed to the old split/alphaextract/blend/alphamerge/
    ///     overlay dance that manually re-masked `blend`'s alpha-ignorant
    ///     output back down to the channel's real alpha.
    ///   - `Channel.BlendMode` (VideoChannel.BlendMode, post schema rewrite)
    ///     is typed as `ChannelBlendMode` — a superset wrapper around
    ///     SKBlendMode (see ChannelBlendMode.cs) rather than SKBlendMode
    ///     directly, so the four modes with no native Skia equivalent have
    ///     a stable slot to be implemented into later via a custom SkSL
    ///     shader, without another breaking enum swap.
    ///
    /// TASK 7 ADDITION: ToNativeForMerge exposes the same mapping to
    /// ImageGraphEvaluator's MergeNode dispatch (Components/Effects/
    /// VideoEffectNodes.cs) — MergeNode composites two IMAGE streams the
    /// exact same way a channel composites onto the ones beneath it, so it
    /// should speak the identical blend-mode vocabulary rather than
    /// maintaining a second copy of this table.
    /// </summary>
    internal static class ChannelCompositor
    {
        /// <summary>
        /// Explicit mapping rather than relying on the two enums happening
        /// to share ordinal values — ChannelBlendMode's declaration order
        /// mirrors SKBlendMode's today, but an explicit table doesn't
        /// silently break if either enum is ever reordered or Skia adds a
        /// new mode in between.
        /// </summary>
        private static readonly Dictionary<ChannelBlendMode, SKBlendMode> NativeModes = new()
        {
            [ChannelBlendMode.Clear] = SKBlendMode.Clear,
            [ChannelBlendMode.Src] = SKBlendMode.Src,
            [ChannelBlendMode.Dst] = SKBlendMode.Dst,
            [ChannelBlendMode.SrcOver] = SKBlendMode.SrcOver,
            [ChannelBlendMode.DstOver] = SKBlendMode.DstOver,
            [ChannelBlendMode.SrcIn] = SKBlendMode.SrcIn,
            [ChannelBlendMode.DstIn] = SKBlendMode.DstIn,
            [ChannelBlendMode.SrcOut] = SKBlendMode.SrcOut,
            [ChannelBlendMode.DstOut] = SKBlendMode.DstOut,
            [ChannelBlendMode.SrcATop] = SKBlendMode.SrcATop,
            [ChannelBlendMode.DstATop] = SKBlendMode.DstATop,
            [ChannelBlendMode.Xor] = SKBlendMode.Xor,
            [ChannelBlendMode.Plus] = SKBlendMode.Plus,
            [ChannelBlendMode.Modulate] = SKBlendMode.Modulate,
            [ChannelBlendMode.Screen] = SKBlendMode.Screen,
            [ChannelBlendMode.Overlay] = SKBlendMode.Overlay,
            [ChannelBlendMode.Darken] = SKBlendMode.Darken,
            [ChannelBlendMode.Lighten] = SKBlendMode.Lighten,
            [ChannelBlendMode.ColorDodge] = SKBlendMode.ColorDodge,
            [ChannelBlendMode.ColorBurn] = SKBlendMode.ColorBurn,
            [ChannelBlendMode.HardLight] = SKBlendMode.HardLight,
            [ChannelBlendMode.SoftLight] = SKBlendMode.SoftLight,
            [ChannelBlendMode.Difference] = SKBlendMode.Difference,
            [ChannelBlendMode.Exclusion] = SKBlendMode.Exclusion,
            [ChannelBlendMode.Multiply] = SKBlendMode.Multiply,
            [ChannelBlendMode.Hue] = SKBlendMode.Hue,
            [ChannelBlendMode.Saturation] = SKBlendMode.Saturation,
            [ChannelBlendMode.Color] = SKBlendMode.Color,
            [ChannelBlendMode.Luminosity] = SKBlendMode.Luminosity,
 
            // Average, Negation, Divide, Subtract deliberately absent —
            // see ToNative's throw below.
        };
 
        /// <summary>
        /// Draws a finished channel onto the accumulator canvas with the
        /// given blend mode. No `enable`/`setpts` gating needed here, same
        /// as the old Draw's own comment — a clip is only present at all if
        /// it's visible on this frame, gated in C# already.
        /// </summary>
        public static void Draw(SKCanvas canvas, SKImage channel, ChannelBlendMode blendMode)
        {
            using var paint = new SKPaint { BlendMode = ToNativeForMerge(blendMode) };
            canvas.DrawImage(channel, 0, 0, paint);
        }
 
        /// <summary>
        /// Throws for the four reserved arithmetic modes rather than
        /// silently falling back to SrcOver or no-oping — a clip configured
        /// with one of these should get a clear, immediate error, not a
        /// quietly-wrong render. Swap in a real case here (an SKRuntimeEffect
        /// shader) if/when one of these is actually needed; until then this
        /// stays a deliberate gap, not an oversight.
        ///
        /// Internal (not private) — also called directly by
        /// ImageGraphEvaluator for MergeNode, which needs the exact same
        /// ChannelBlendMode -> SKBlendMode mapping this class already owns.
        /// </summary>
        internal static SKBlendMode ToNativeForMerge(ChannelBlendMode mode)
        {
            if (NativeModes.TryGetValue(mode, out SKBlendMode native))
                return native;
 
            throw new NotSupportedException(
                $"ChannelBlendMode.{mode} has no native SKBlendMode implementation. " +
                "This mode is reserved for a future SKRuntimeEffect (SkSL) shader " +
                "and has not been built — see ChannelBlendMode.cs.");
        }
    }
}
 