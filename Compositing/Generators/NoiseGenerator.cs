using System;
using SkiaSharp;
using EditSharp.Components.Nodes.Sources;
using EditSharp.Compositing.Gpu;
 
namespace EditSharp.Compositing.Generators
{
    /// <summary>
    /// Real-time procedural noise via an SkSL shader — no pre-render, no
    /// seek, nothing to pre-render at all. Rendered on the GPU (via the
    /// shared SurfacePool) like everything else in the compositor.
    ///
    /// HONESTY FLAG, carried over unchanged: this is a standard
    /// gradient-noise (Perlin-style) implementation, NOT a byte-exact port
    /// of ffmpeg's own `perlin` filter algorithm.
    ///
    /// "CLIPS ARE GRAPHS" REWRITE: takes a NoiseInputNode instead of the old
    /// NoiseClip (Detail/SeetheRate/Seed field names unchanged) — no other
    /// change needed here. Already rendered directly at canvas resolution
    /// (unlike ColorGeneratorInputNode, which changed from a 1x1 fill to
    /// canvas-sized for this same rewrite — see ColorGenerator's own
    /// remarks), so this class's own contract is unaffected by the "native
    /// size now comes from the resolved image itself" change.
    ///
    /// UNIFORM LAYOUT, DELIBERATELY THREE vec4's INSTEAD OF SIX SEPARATE
    /// float/float2/float3 UNIFORMS — every uniform is naturally aligned to
    /// a 16-byte boundary with nothing left for a constant-buffer packer to
    /// get creative about. A real hardening measure, kept even though it
    /// turned out not to be the cause of the block-corruption bug under
    /// active investigation (see SurfacePool's own remarks for where that
    /// investigation currently stands). u2 only uses its first component
    /// (seedOffset.z) — the trailing padding is intentional, not leftover.
    ///
    /// ROOT CAUSE OF THE BLOCK-CORRUPTION BUG, and the two changes that
    /// fix it. Established by controlled A/B on ONE machine (Framework
    /// Laptop 16), ONE build, at ONE moment, selecting the GPU with
    /// EDITSHARP_D3D12_ADAPTER:
    ///     AMD Radeon 890M  -> fully correct, no bisect stage diverges
    ///     NVIDIA RTX 5070  -> blocked noise, bisect diverges at stage 7
    /// Same binary, same blueprint, same instant. That excludes hardware,
    /// driver version (multiple were tried), the D3D12 context, the surface
    /// pool, uniform binding, and every other shared code path at once, and
    /// leaves exactly one thing: this shader contains arithmetic whose
    /// result depends on compiler rounding choices, and NVIDIA's compiler
    /// makes different (entirely legal) choices than AMD's.
    ///
    /// (1) THE HASH — the amplifier, and the reason the corruption is
    /// visible at all. The old hash ended with:
    ///         return fract((p.x + p.y) * p.z);   // argument approx 5000
    /// fract() of a value near 5000, where fp32's ULP is about 4.9e-4. Any
    /// one-ULP difference in how the chain is evaluated — FMA contraction,
    /// reassociation, both legal and both invisible in source — nudges that
    /// argument. Nearly always harmless; but whenever it sits within a few
    /// ULP of an integer, fract() flips between ~0.999 and ~0.001 and that
    /// lattice corner's gradient becomes COMPLETELY different. One poisoned
    /// corner corrupts the up-to-eight cells sharing it, and each cell is
    /// tens of pixels wide: a scatter of grossly wrong rectangles in an
    /// otherwise correct field. Deterministic (same coordinates every
    /// frame), vendor-specific, magnitude-sensitive, and completely
    /// invisible to the D3D12 validation layer because nothing about it is
    /// an API error.
    ///     FIX: an exact-integer hash (mod289/permute, below). Every
    ///     intermediate is an fp32 value that is mathematically an integer
    ///     below 2^24, and fp32 represents those EXACTLY — so there is no
    ///     rounding for any compiler to disagree about. Bit-identical on
    ///     NVIDIA, AMD, and the CPU rasterizer, by construction.
    ///
    /// (2) floor/fract CONSISTENCY — a second, independent hazard, fixed by
    /// `float3 f = p - i;` in gradientNoise3D. `floor(p)` and `fract(p)`
    /// were two separate reads of an INLINED expression
    /// (`xscale * (fragCoord.x / resolution.x) + seedOffset.x`), which a
    /// compiler may contract into an FMA at one site and not the other. The
    /// two values then differ by ~1 ULP, and a pixel near a cell boundary
    /// gets its cell index from one and its sub-cell fraction from the
    /// other — gradient from cell N, weight from cell N+1, a hard seam
    /// along the whole shared edge. Deriving f FROM i makes i + f == p hold
    /// bit-exactly whatever the compiler materialises, and keeps the corner
    /// arithmetic (i + offset against f - offset) complementary. Applied
    /// first, on its own it was necessary but NOT sufficient: it removes
    /// the boundary seams, while (1) removes the poisoned cells.
    ///
    /// Both changes are free — a subtract replaces a fract, and the integer
    /// hash is comparable arithmetic — and both are the right discipline for
    /// every future shader on this path, keying included: never let a
    /// visible result depend on the low bits of a large float, and never
    /// compute two quantities that must agree as independent expressions.
    ///
    /// NOTE: the generated pattern is DIFFERENT from the old hash's for the
    /// same Seed. Detail/SeetheRate/Seed semantics are unchanged and seeds
    /// remain well distributed; existing projects will see their noise
    /// change appearance once.
    ///
    /// FALSIFIED ALONG THE WAY, recorded so none of it is re-litigated:
    /// failing hardware (reproduced on a second, factory-fresh card);
    /// driver regression (multiple driver versions, including several
    /// 5xx.xx, all reproduce); compositor/tint/transform stages (raw
    /// pre-composite dumps were already corrupted); uninitialised VRAM,
    /// races and submission ordering (corruption is bit-identical across
    /// separate runs); the mipmap-generation path (a real bug, found and
    /// fixed, validation layer now clean — but not this); shader float
    /// precision in the shallow case (a fract(u*30 + 1000.0) probe came
    /// back smooth and bit-identical on both backends); and uniform
    /// binding (stage 6 echoed all three uniforms as a flat colour,
    /// bit-identical GPU vs CPU).
    /// </summary>
    internal static class NoiseGenerator
    {
        private const double DetailCellsPerCanvas = 1000.0;
        private const double SeetheCellsPerSecond = 10.0;
 
        private const string ShaderSource = """
            uniform float4 u0; // resolution.x, resolution.y, xscale, yscale
            uniform float4 u1; // tscale, time, seedOffset.x, seedOffset.y
            uniform float4 u2; // seedOffset.z, unused, unused, unused

            // EXACT-INTEGER HASH. Every intermediate below is an fp32
            // value that is mathematically an integer smaller than 2^24,
            // and fp32 represents such integers EXACTLY. There is no
            // rounding anywhere in this chain, so no compiler is free to
            // produce a different answer: identical results on NVIDIA, on
            // AMD, and on the CPU rasterizer. This replaces a fract()-based
            // float hash whose final step took fract() of a value around
            // 5000 (ULP ~4.9e-4) — see this file's ROOT CAUSE remarks.
            //
            // Worst-case magnitude check (must stay under 16,777,216):
            //   permute input  < 581
            //   (581*34 + 1) * 581 = 11,477,655            OK
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
                // f is derived FROM i, never computed independently via
                // fract(p) — see this file's ROOT CAUSE remarks. This one
                // line is the fix for the block-seam corruption.
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
                throw new InvalidOperationException($"NoiseInputNode shader failed to compile: {errors}");
            return effect;
        }
 
        public static SKImage Render(
            NoiseInputNode node, double clipSeconds, int canvasWidth, int canvasHeight, SurfacePool pool)
        {
            double xscale = Math.Max(node.Detail, 0f) * DetailCellsPerCanvas;
            double yscale = xscale * canvasHeight / (double)canvasWidth;
            double tscale = Math.Max(node.SeetheRate, 0f) * SeetheCellsPerSecond;
 
            var rng = new Random(node.Seed);
            // 289 is the exact-integer hash's wrap period (see the shader):
            // offsets beyond it add no new lattice variety, and a smaller
            // magnitude leaves more mantissa for the sub-cell fraction.
            float seedOffsetX = (float)(rng.NextDouble() * 289.0);
            float seedOffsetY = (float)(rng.NextDouble() * 289.0);
            float seedOffsetZ = (float)(rng.NextDouble() * 289.0);
 
            float[] u0 = [canvasWidth, canvasHeight, (float)xscale, (float)yscale];
            float[] u1 = [(float)tscale, (float)clipSeconds, seedOffsetX, seedOffsetY];
            float[] u2 = [seedOffsetZ, 0f, 0f, 0f];
 
            var uniforms = new SKRuntimeEffectUniforms(Effect)
            {
                ["u0"] = u0,
                ["u1"] = u1,
                ["u2"] = u2,
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
 