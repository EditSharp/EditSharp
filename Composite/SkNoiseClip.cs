using System;
using System.IO;
using System.Threading;
using SkiaSharp;
using EditSharp.Components.Nodes.Sources.Video;

namespace EditSharp.Composite
{
    /// <summary>
    /// Real-time procedural noise via an SkSL shader — no pre-render, no
    /// seek, nothing to pre-render at all. Rendered on the GPU (via the
    /// shared SkSurfacePool) like everything else in the compositor.
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
    /// float/float2/float3 UNIFORMS — every uniform is naturally aligned to
    /// a 16-byte boundary with nothing left for a constant-buffer packer to
    /// get creative about. A real hardening measure, kept even though it
    /// turned out not to be the cause of the block-corruption bug under
    /// active investigation (see SkSurfacePool's own remarks for where that
    /// investigation currently stands). u2 only uses its first component
    /// (seedOffset.z) — the trailing padding is intentional, not leftover.
    ///
    /// ROOT CAUSE OF THE BLOCK-CORRUPTION BUG — FOUND, AND FIXED BELOW BY
    /// THE SINGLE LINE `float3 f = p - i;` IN gradientNoise3D.
    ///
    /// The shader used to compute the lattice cell and the position within
    /// that cell as two INDEPENDENT expressions:
    ///     float3 i = floor(p);
    ///     float3 f = fract(p);   // <-- the bug
    /// Both look like they read the same `p`, but `p` here is not a stored
    /// value — it is the inlined expression
    /// `xscale * (fragCoord.x / resolution.x) + seedOffset.x` (and the y/t
    /// equivalents). A shader compiler is free to contract that multiply-add
    /// into a single FMA instruction at one use site and emit a separate
    /// multiply and add at the other. The two `p` values then differ by
    /// about one ULP. For the vast majority of pixels that is invisible —
    /// but for any pixel lying near a lattice boundary it means floor()
    /// reports cell N while fract() returns a fraction belonging to cell
    /// N+1. The gradient is then fetched for one cell and interpolated with
    /// the other cell's weight, producing a hard discontinuity along the
    /// whole shared edge: a rectangular SEAM, not isolated speckle.
    ///
    /// HOW THIS WAS ESTABLISHED (SkNoiseShaderBisect, run against real
    /// hardware — every earlier theory was falsified by measurement first):
    ///   - compositor/tint/transform stages: excluded (raw pre-composite
    ///     dumps were already corrupted)
    ///   - uninitialized VRAM / races / submission ordering: excluded (the
    ///     corruption is bit-identical across separate runs)
    ///   - the mipmap-generation path: a real bug, found and fixed, but not
    ///     this one (D3D12 validation layer went fully clean, corruption
    ///     remained)
    ///   - shader float precision: excluded (fract(u*30 + 1000.0) came back
    ///     smooth and bit-identical on GPU and CPU)
    ///   - uniform binding: excluded (stage 6 echoed all three uniforms as a
    ///     flat colour, bit-identical GPU vs CPU)
    ///   - fract(p) ALONE (stage 1): clean. floor(p)-derived gradient ALONE
    ///     (stage 4): clean. The FIRST stage to use i and f TOGETHER
    ///     (stage 7, dot(gradient(i), f)): DIVERGED. That is the whole
    ///     result — each half is individually correct, and only their
    ///     combination is wrong, which is precisely what an i/f
    ///     disagreement looks like and nothing else does.
    ///
    /// Deriving f from i (f = p - i) makes the two exactly complementary by
    /// construction: whatever value of p the compiler materialises,
    /// i + f == p holds bit-exactly, and the corner arithmetic
    /// (i + offset paired with f - offset) stays consistent. The seams
    /// cannot form. There is no performance cost — a subtract replaces a
    /// fract — and the same discipline protects every future shader on this
    /// path, keying included.
    /// </summary>
    internal static class SkNoiseClip
    {
        private const double DetailCellsPerCanvas = 1000.0;
        private const double SeetheCellsPerSecond = 10.0;

        // ---------------------------------------------------------------
        // TEMPORARY DIAGNOSTIC (re-enabled for the DETERMINISM test) —
        // remove once root-caused. Set EDITSHARP_NOISE_DEBUG_DUMP to a
        // folder and the first 120 noise frames are written to PNG the
        // instant they come back from the GPU draw, before anything else
        // touches them.
        //
        // The question this round is NOT "is it corrupted" (already
        // established: yes, from frame one) but "is the corruption
        // BIT-IDENTICAL between two separate runs of the same blueprint."
        // Identical => a deterministic logic bug (uniform/descriptor/
        // binding — something computed wrong the same way every time).
        // Different => uninitialized VRAM or a genuine race, which points
        // at the RenderTargetOrDepthStencilResouceNotInitialized errors
        // still in the D3D12 log instead. Those two causes need opposite
        // fixes, so this distinction decides the whole next step.
        // ---------------------------------------------------------------
        private static readonly string? DebugDumpDir =
            Environment.GetEnvironmentVariable("EDITSHARP_NOISE_DEBUG_DUMP");
        private static int _debugDumpCount;
        private const int DebugDumpMax = 120;

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
            NoiseInputNode node, double clipSeconds, int canvasWidth, int canvasHeight, SkSurfacePool pool)
        {
            double xscale = Math.Max(node.Detail, 0f) * DetailCellsPerCanvas;
            double yscale = xscale * canvasHeight / (double)canvasWidth;
            double tscale = Math.Max(node.SeetheRate, 0f) * SeetheCellsPerSecond;

            var rng = new Random(node.Seed);
            float seedOffsetX = (float)(rng.NextDouble() * 1000.0);
            float seedOffsetY = (float)(rng.NextDouble() * 1000.0);
            float seedOffsetZ = (float)(rng.NextDouble() * 1000.0);

            float[] u0 = [canvasWidth, canvasHeight, (float)xscale, (float)yscale];
            float[] u1 = [(float)tscale, (float)clipSeconds, seedOffsetX, seedOffsetY];
            float[] u2 = [seedOffsetZ, 0f, 0f, 0f];

            // TEMPORARY DIAGNOSTIC — no-op unless EDITSHARP_SHADER_BISECT
            // is set. Deliberately passed the SAME uniform arrays the real
            // draw below uses. See SkNoiseShaderBisect's own remarks.
            SkNoiseShaderBisect.RunOnce(canvasWidth, canvasHeight, pool, u0, u1, u2);

            var uniforms = new SKRuntimeEffectUniforms(Effect)
            {
                ["u0"] = u0,
                ["u1"] = u1,
                ["u2"] = u2,
            };

            using SKShader shader = Effect.ToShader(uniforms);
            using var paint = new SKPaint { Shader = shader };

            SKImage result;
            SKSurface surface = pool.Rent(canvasWidth, canvasHeight);
            try
            {
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.DrawRect(new SKRect(0, 0, canvasWidth, canvasHeight), paint);
                result = surface.Snapshot();
            }
            finally
            {
                pool.Return(surface, canvasWidth, canvasHeight);
            }

            if (DebugDumpDir != null) DumpDebugFrame(result, clipSeconds);

            return result;
        }

        /// <summary>See the TEMPORARY DIAGNOSTIC remarks above this class's fields.</summary>
        private static void DumpDebugFrame(SKImage image, double clipSeconds)
        {
            int index = Interlocked.Increment(ref _debugDumpCount);
            if (index > DebugDumpMax) return;

            try
            {
                Directory.CreateDirectory(DebugDumpDir!);
                string path = Path.Combine(DebugDumpDir!, $"noise_raw_{index:0000}_{clipSeconds:F3}s.png");

                using SKData? png = image.Encode(SKEncodedImageFormat.Png, 100);
                if (png != null) File.WriteAllBytes(path, png.ToArray());
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogWarning($"SkNoiseClip debug dump failed: {ex.Message}");
            }
        }
    }
}