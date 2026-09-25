using System;
using System.Collections.Generic;
using SkiaSharp;
using EditSharp.Components;
using EditSharp.Components.Channels;

namespace EditSharp.Compositing
{
    /// <summary>Draws finished channels onto the frame with their blend modes.</summary>
    /// <remarks>Most modes are Skia's own. Average, Negation, Divide and Subtract have no Skia equivalent and are SkSL blenders using the W3C separable blend formula. MergeNode uses the same mapping.</remarks>
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
        };

        //each blend function of the unpremultiplied colours: cb the layers below, cs the layer drawn
        private static readonly Dictionary<ChannelBlendMode, SKBlender> Blenders = new()
        {
            [ChannelBlendMode.Average] = CreateBlender("(cb + cs) * 0.5"),
            [ChannelBlendMode.Negation] = CreateBlender("1 - abs(1 - cb - cs)"),
            [ChannelBlendMode.Divide] = CreateBlender("cb / max(cs, 0.0001)"),
            [ChannelBlendMode.Subtract] = CreateBlender("cb - cs"),
        };

        //the W3C separable blend: B(cb, cs) where both layers are opaque, each layer alone where the other isn't
        private static SKBlender CreateBlender(string blend)
        {
            string source = $$"""
                half4 main(half4 src, half4 dst) {
                    half3 cs = src.a > 0 ? src.rgb / src.a : half3(0);
                    half3 cb = dst.a > 0 ? dst.rgb / dst.a : half3(0);
                    half3 b = saturate({{blend}});
                    return half4(src.rgb * (1 - dst.a) + dst.rgb * (1 - src.a) + src.a * dst.a * b, src.a + dst.a * (1 - src.a));
                }
                """;

            using SKRuntimeEffect effect = SKRuntimeEffect.CreateBlender(source, out string errors)
                ?? throw new InvalidOperationException($"The blend shader failed to compile: {errors}");
            return effect.ToBlender();
        }

        //draws a finished channel onto the frame with its blend mode
        public static void Draw(SKCanvas canvas, SKImage channel, ChannelBlendMode blendMode)
        {
            using var paint = new SKPaint();
            ApplyBlend(paint, blendMode);
            canvas.DrawImage(channel, 0, 0, paint);
        }

        //sets `paint` to draw with a ChannelBlendMode
        internal static void ApplyBlend(SKPaint paint, ChannelBlendMode mode)
        {
            if (NativeModes.TryGetValue(mode, out SKBlendMode native)) paint.BlendMode = native;
            else if (Blenders.TryGetValue(mode, out SKBlender? blender)) paint.Blender = blender;
            else throw new NotSupportedException($"ChannelBlendMode.{mode} isn't a known blend mode.");
        }
    }
}
