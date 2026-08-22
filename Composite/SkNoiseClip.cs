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
    ///
    /// UNIFORM LAYOUT, DELIBERATELY THREE vec4's INSTEAD OF SIX SEPARATE
    /// float/float2/float3 UNIFORMS — this is a real fix, not a style
    /// preference. This is the ONLY draw in the whole compositor that uses
    /// a custom multi-uniform SKRuntimeEffect (every other draw is a plain
    /// DrawImage of pre-resolved pixels with no uniform block at all), and
    /// it's the one place a real, reported bug showed up: on the D3D12
    /// GRContext backend (see GpuContext), a scalar/vec2/vec3 uniform
    /// sequence (float2, float, float, float, float, float3 — the original
    /// shape here) can straddle HLSL's 16-byte constant-buffer register
    /// boundaries depending on exactly how the backend packs them, which is
    /// precisely the kind of thing that can differ by GPU vendor/driver
    /// even on the same backend — confirmed in the field as visible
    /// rectangular block corruption in the noise output on an NVIDIA GPU
    /// (where the real D3D12 GPU shader path actually runs) while the same
    /// content rendered correctly on hardware that fell back to software
    /// rasterization instead (see GpuContext's own remarks: D3D12 device
    /// creation failing at all falls straight to software with no ANGLE/GL
    /// middle ground, and a weaker/older iGPU is a plausible place for that
    /// to happen) — i.e. the bug only manifests wherever the real GPU
    /// uniform-packing path is actually exercised, not from anything
    /// specific to noise's own math.
    ///
    /// Packing every uniform into three vec4's (u0/u1/u2), each exactly 16
    /// bytes, removes the ambiguity entirely: every uniform is now
    /// naturally aligned to its own HLSL register with nothing left for a
    /// packer to get creative about, regardless of backend or vendor. u2
    /// only uses its first component (seedOffset.z) — the trailing padding
    /// is intentional, not leftover.
    /// </summary>
    internal static class SkNoiseClip
    {
        private const double DetailCellsPerCanvas = 1000.0;
        private const double SeetheCellsPerSecond = 10.0;

        private const string ShaderSource = """
            uniform float4 u0; // resolution.x, resolution.y, xscale, yscale
            uniform float4 u1; // tscale, time, seedOffset.x, seedOffset.y
            uniform float4 u2; // seedOffset.z, unused, unused, unused

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
                float2 resolution = u0.xy;
                float xscale = u0.z;
                float yscale = u0.w;
                float tscale = u1.x;
                float time = u1.y;
                float3 seedOffset = float3(u1.z, u1.w, u2.x);

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
            float seedOffsetX = (float)(rng.NextDouble() * 1000.0);
            float seedOffsetY = (float)(rng.NextDouble() * 1000.0);
            float seedOffsetZ = (float)(rng.NextDouble() * 1000.0);

            var uniforms = new SKRuntimeEffectUniforms(Effect)
            {
                ["u0"] = new float[] { canvasWidth, canvasHeight, (float)xscale, (float)yscale },
                ["u1"] = new float[] { (float)tscale, (float)clipSeconds, seedOffsetX, seedOffsetY },
                ["u2"] = new float[] { seedOffsetZ, 0f, 0f, 0f },
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