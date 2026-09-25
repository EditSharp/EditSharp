using System;
using SkiaSharp;

namespace EditSharp.Compositing.Generators
{
    /// <summary>Gradient (Perlin-style) noise drawn by an SkSL shader on the GPU, evolving over content time.</summary>
    /// <remarks>
    /// Two rules keep it identical on every GPU and on the CPU rasterizer. The
    /// lattice hash uses only integers below 2^24, which fp32 holds exactly, so no
    /// compiler rounding can change it; a hash that took fract() of a large float
    /// gave NVIDIA and AMD different gradients at some lattice points, which
    /// showed as wrong rectangles. And the fraction within a cell is computed as
    /// p minus the cell index, never separately with fract(p), so the two always
    /// agree at cell edges. The uniforms are packed as three vec4s so none needs
    /// padding. It isn't a copy of ffmpeg's perlin filter.
    /// </remarks>
    internal static class NoiseGenerator
    {
        private const double DetailCellsPerCanvas = 1000.0;
        private const double SeetheCellsPerSecond = 10.0;

        private const string ShaderSource = """
            uniform float4 u0; // resolution.x, resolution.y, xscale, yscale
            uniform float4 u1; // tscale, time, seedOffset.x, seedOffset.y
            uniform float4 u2; // seedOffset.z, unused, unused, unused

            // exact-integer hash: every intermediate is an integer below 2^24, which fp32 holds exactly,
            // so every GPU and the CPU compute the same value.
            // Largest value: permute input < 581; (581*34 + 1) * 581 = 11,477,655 < 16,777,216
            float mod289(float x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
            float permute(float x) { return mod289(((x * 34.0) + 1.0) * x); }

            // `p` must be an integer lattice coordinate. `salt` selects one
            // of several independent streams for the same lattice point.
            float hash31(float3 p, float salt) {
                float h = permute(
                    permute(permute(mod289(p.x)) + mod289(p.y)) + mod289(p.z) + salt);
                return h * (1.0 / 289.0);
            }

            float3 gradient(float3 p) {
                return float3(
                    hash31(p, 0.0),
                    hash31(p, 1.0),
                    hash31(p, 2.0)
                ) * 2.0 - 1.0;
            }

            float fade(float t) {
                return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
            }

            float gradientNoise3D(float3 p) {
                float3 i = floor(p);
                // f from i, not fract(p), so i + f == p exactly and neighbouring cells agree at their edge
                float3 f = p - i;
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
                throw new InvalidOperationException($"The noise shader failed to compile: {errors}");
            return effect;
        }

        /// <summary>
        /// Draws the noise field for `seconds` of content time over a
        /// width x height canvas. `detail` sets the cell size relative to the
        /// canvas, `seethe` how fast it evolves; `seed` picks the pattern.
        /// </summary>
        public static void Draw(SKCanvas canvas, int seed, float detail, float seethe, double seconds, int width, int height)
        {
            double xscale = Math.Max(detail, 0f) * DetailCellsPerCanvas;
            double yscale = xscale * height / (double)width;
            double tscale = Math.Max(seethe, 0f) * SeetheCellsPerSecond;

            //289 is the lattice hash's wrap period: larger offsets add nothing
            var rng = new Random(seed);
            float seedOffsetX = (float)(rng.NextDouble() * 289.0);
            float seedOffsetY = (float)(rng.NextDouble() * 289.0);
            float seedOffsetZ = (float)(rng.NextDouble() * 289.0);

            var uniforms = new SKRuntimeEffectUniforms(Effect)
            {
                ["u0"] = new float[] { width, height, (float)xscale, (float)yscale },
                ["u1"] = new float[] { (float)tscale, (float)seconds, seedOffsetX, seedOffsetY },
                ["u2"] = new float[] { seedOffsetZ, 0f, 0f, 0f },
            };

            using SKShader shader = Effect.ToShader(uniforms);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(new SKRect(0, 0, width, height), paint);
        }
    }
}
