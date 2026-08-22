using System;
using SkiaSharp;
using EditSharp.Components.Nodes.Sources.Video;
 
namespace EditSharp.Composite
{
    /// <summary>
    /// Real-time procedural noise via an SkSL shader — no pre-render, no
    /// seek, nothing to pre-render at all.
    ///
    /// HONESTY FLAG, carried over unchanged: this is a standard
    /// gradient-noise (Perlin-style) implementation, NOT a byte-exact port
    /// of ffmpeg's own `perlin` filter algorithm.
    ///
    /// "CLIPS ARE GRAPHS" REWRITE: takes a NoiseInputNode instead of the old
    /// NoiseClip (Detail/SeetheRate/Seed field names unchanged) — no other
    /// change needed here. Already rendered directly at canvas resolution
    /// (unlike ColorGeneratorInputNode, which changed from a 1x1 fill to
    /// canvas-sized for this same rewrite — see SkGeneratorClip's own
    /// remarks), so this class's own contract is unaffected by the "native
    /// size now comes from the resolved image itself" change.
    /// </summary>
    internal static class SkNoiseClip
    {
        private const double DetailCellsPerCanvas = 1000.0;
        private const double SeetheCellsPerSecond = 10.0;
 
        private const string ShaderSource = """
            uniform float2 resolution;
            uniform float xscale;
            uniform float yscale;
            uniform float tscale;
            uniform float time;
            uniform float3 seedOffset;

            float hash31(float3 p) {
                p = fract(p * float3(0.1031, 0.1030, 0.0973));
                p += dot(p, p.yzx + 33.33);
                return fract((p.x + p.y) * p.z);
            }

            float3 gradient(float3 p) {
                return float3(
                    hash31(p + float3(17.0, 0.0, 0.0)),
                    hash31(p + float3(0.0, 17.0, 0.0)),
                    hash31(p + float3(0.0, 0.0, 17.0))
                ) * 2.0 - 1.0;
            }

            float fade(float t) {
                return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
            }

            float gradientNoise3D(float3 p) {
                float3 i = floor(p);
                float3 f = fract(p);
                float3 u = float3(fade(f.x), fade(f.y), fade(f.z));

                float n000 = dot(gradient(i + float3(0.0, 0.0, 0.0)), f - float3(0.0, 0.0, 0.0));
                float n100 = dot(gradient(i + float3(1.0, 0.0, 0.0)), f - float3(1.0, 0.0, 0.0));
                float n010 = dot(gradient(i + float3(0.0, 1.0, 0.0)), f - float3(0.0, 1.0, 0.0));
                float n110 = dot(gradient(i + float3(1.0, 1.0, 0.0)), f - float3(1.0, 1.0, 0.0));
                float n001 = dot(gradient(i + float3(0.0, 0.0, 1.0)), f - float3(0.0, 0.0, 1.0));
                float n101 = dot(gradient(i + float3(1.0, 0.0, 1.0)), f - float3(1.0, 0.0, 1.0));
                float n011 = dot(gradient(i + float3(0.0, 1.0, 1.0)), f - float3(0.0, 1.0, 1.0));
                float n111 = dot(gradient(i + float3(1.0, 1.0, 1.0)), f - float3(1.0, 1.0, 1.0));

                float nx00 = mix(n000, n100, u.x);
                float nx10 = mix(n010, n110, u.x);
                float nx01 = mix(n001, n101, u.x);
                float nx11 = mix(n011, n111, u.x);

                float nxy0 = mix(nx00, nx10, u.y);
                float nxy1 = mix(nx01, nx11, u.y);

                return mix(nxy0, nxy1, u.z);
            }

            half4 main(float2 fragCoord) {
                float x = xscale * (fragCoord.x / resolution.x) + seedOffset.x;
                float y = yscale * (fragCoord.y / resolution.y) + seedOffset.y;
                float t = (tscale * time) + seedOffset.z;

                float n = gradientNoise3D(float3(x, y, t));

                float v = clamp((n * 0.5) + 0.5, 0.0, 1.0);

                return half4(v, v, v, 1.0);
            }
            """;
 
        private static readonly SKRuntimeEffect Effect = CreateEffect();
 
        private static SKRuntimeEffect CreateEffect()
        {
            SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(ShaderSource, out string errors);
            if (effect == null)
                throw new InvalidOperationException($"NoiseInputNode shader failed to compile: {errors}");
            return effect;
        }
 
        public static SKImage Render(
            NoiseInputNode node, double clipSeconds, int canvasWidth, int canvasHeight, SkSurfacePool pool)
        {
            double xscale = Math.Max(node.Detail, 0f) * DetailCellsPerCanvas;
            double yscale = xscale * canvasHeight / (double)canvasWidth;
            double tscale = Math.Max(node.SeetheRate, 0f) * SeetheCellsPerSecond;
 
            var rng = new Random(node.Seed);
            float[] seedOffset =
            [
                (float)(rng.NextDouble() * 1000.0),
                (float)(rng.NextDouble() * 1000.0),
                (float)(rng.NextDouble() * 1000.0),
            ];
 
            var uniforms = new SKRuntimeEffectUniforms(Effect)
            {
                ["resolution"] = new float[] { canvasWidth, canvasHeight },
                ["xscale"] = (float)xscale,
                ["yscale"] = (float)yscale,
                ["tscale"] = (float)tscale,
                ["time"] = (float)clipSeconds,
                ["seedOffset"] = seedOffset,
            };
 
            using SKShader shader = Effect.ToShader(uniforms);
            using var paint = new SKPaint { Shader = shader };
 
            SKSurface surface = pool.Rent(canvasWidth, canvasHeight);
            try
            {
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.DrawRect(new SKRect(0, 0, canvasWidth, canvasHeight), paint);
                return surface.Snapshot();
            }
            finally
            {
                pool.Return(surface, canvasWidth, canvasHeight);
            }
        }
    }
}
 