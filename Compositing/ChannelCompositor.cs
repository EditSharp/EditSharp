using System;
using System.Collections.Generic;
using SkiaSharp;
using EditSharp.Components;
using EditSharp.Components.Channels;

namespace EditSharp.Compositing
{
    /// <summary>Draws finished channels onto the frame with their blend modes, through Skia's alpha-aware blend modes.</summary>
    /// <remarks>ChannelBlendMode wraps SKBlendMode and adds four arithmetic modes Skia lacks, reserved for custom shaders; they throw until then. MergeNode uses the same mapping.</remarks>
    internal static class ChannelCompositor
    {
        //a table, not a cast, so reordering either enum can't silently change a mode
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

            //Average, Negation, Divide and Subtract have no Skia equivalent; see ToNativeForMerge
        };

        //draws a finished channel onto the frame with its blend mode
        public static void Draw(SKCanvas canvas, SKImage channel, ChannelBlendMode blendMode)
        {
            using var paint = new SKPaint { BlendMode = ToNativeForMerge(blendMode) };
            canvas.DrawImage(channel, 0, 0, paint);
        }

        //the Skia mode for a ChannelBlendMode; the four reserved arithmetic modes throw rather than render wrongly
        internal static SKBlendMode ToNativeForMerge(ChannelBlendMode mode)
        {
            if (NativeModes.TryGetValue(mode, out SKBlendMode native))
                return native;

            throw new NotSupportedException(
                $"ChannelBlendMode.{mode} has no native SKBlendMode implementation. " +
                "This mode is reserved for a future SKRuntimeEffect (SkSL) shader " +
                "and hasn't been built yet.");
        }
    }
}
