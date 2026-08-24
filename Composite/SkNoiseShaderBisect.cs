using System;
using System.IO;
using SkiaSharp;

namespace EditSharp.Composite
{
    /// <summary>
    /// TEMPORARY DIAGNOSTIC — bisects SkNoiseClip's shader to find the
    /// exact stage at which GPU execution diverges from CPU execution.
    ///
    /// WHY THIS, AND WHY NOW. Everything cheap has been eliminated by real
    /// measurement, in order:
    ///   - compositor/blend/tint stages (raw pre-composite dumps already
    ///     corrupted)
    ///   - uninitialized VRAM, races, submission ordering (corruption is
    ///     bit-identical across separate runs — deterministic)
    ///   - the mipmap-generation path (fixed; D3D12 validation layer now
    ///     clean of ResourceBarrier/InvalidSubresourceState entirely)
    ///   - shader floating-point precision (a probe rendering
    ///     fract(u*30 + 1000.0) came back smooth and bit-identical on GPU
    ///     and CPU — full fp32 confirmed)
    /// That precision probe also, incidentally, proved the whole
    /// SKRuntimeEffect -> ToShader -> SKPaint -> DrawRect -> pooled GPU
    /// surface path works correctly WITH a float4 uniform. So the machinery
    /// is sound; something specific to THIS shader is not.
    ///
    /// Two things distinguish the noise shader from that working probe:
    /// it binds THREE uniforms (u0/u1/u2) rather than one, and it is far
    /// more complex (24 hash31 calls, 8 gradient calls, 7 mixes). This
    /// diagnostic separates those by rendering the real shader's real
    /// uniforms at selectable intermediate stages, on BOTH backends, so the
    /// first stage whose GPU and CPU output differ localizes the fault
    /// precisely instead of by inference.
    ///
    /// STAGES (u3.x selects; each written twice, _gpu and _cpu):
    ///   0 final     — the finished noise; the known-bad baseline
    ///   1 fracts    — fract(p) as RGB; must be a smooth per-pixel ramp
    ///   2 lattice   — floor(p) wrapped to a visible range; flat cells
    ///   3 hash      — hash31(floor(p)); flat per-cell pseudorandom values
    ///   4 gradient  — gradient(floor(p)) remapped to 0..1
    ///   5 fade      — the fade() interpolation weights; smooth ramps
    ///   6 uniforms  — echoes xscale / seedOffset.x / seedOffset.z as flat
    ///                 colour, which DIRECTLY verifies whether u0, u1 and
    ///                 u2 all arrive intact (this is the multi-uniform
    ///                 binding test the single-uniform probe could not do)
    ///
    /// Stage 6 is the highest-value frame: it is a flat colour whose exact
    /// RGB encodes three values pulled from three different uniforms. If
    /// GPU and CPU disagree there, uniform binding is the bug and the
    /// shader maths is fine. If they agree there but diverge at 1-5, the
    /// maths is being miscompiled and the stage number says where.
    /// </summary>
    internal static class SkNoiseShaderBisect
    {
        private const string BisectSource = """
            uniform float4 u0; // resolution.x, resolution.y, xscale, yscale
            uniform float4 u1; // tscale, time, seedOffset.x, seedOffset.y
            uniform float4 u2; // seedOffset.z, unused, unused, unused
            uniform float4 u3; // stage, unused, unused, unused

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

            // The FIXED variant: f derived from i so the two can never
            // disagree about which cell a pixel belongs to. Stage 12.
            float gradientNoise3DFixed(float3 p) {
                float3 i = floor(p);
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

            // The ORIGINAL, broken variant — kept so stage 0 still
            // reproduces the bug side by side with stage 12's fix.
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
                float stage = u3.x;

                float x = xscale * (fragCoord.x / resolution.x) + seedOffset.x;
                float y = yscale * (fragCoord.y / resolution.y) + seedOffset.y;
                float t = (tscale * time) + seedOffset.z;
                float3 p = float3(x, y, t);

                if (stage > 0.5 && stage < 1.5) {
                    float3 f = fract(p);
                    return half4(f.x, f.y, f.z, 1.0);
                }
                if (stage > 1.5 && stage < 2.5) {
                    float3 i = floor(p);
                    return half4(fract(i.x / 16.0), fract(i.y / 16.0), 0.0, 1.0);
                }
                if (stage > 2.5 && stage < 3.5) {
                    float h = hash31(floor(p));
                    return half4(h, h, h, 1.0);
                }
                if (stage > 3.5 && stage < 4.5) {
                    float3 g = gradient(floor(p));
                    return half4(g.x * 0.5 + 0.5, g.y * 0.5 + 0.5, g.z * 0.5 + 0.5, 1.0);
                }
                if (stage > 4.5 && stage < 5.5) {
                    float3 f = fract(p);
                    return half4(fade(f.x), fade(f.y), fade(f.z), 1.0);
                }
                if (stage > 5.5 && stage < 6.5) {
                    return half4(xscale / 100.0, seedOffset.x / 1000.0, seedOffset.z / 1000.0, 1.0);
                }

                // --- COMPLEXITY BISECT of gradientNoise3D itself ---
                // Stage 4 proved ONE gradient() call is correct on GPU.
                // The full function makes EIGHT plus seven mixes and is
                // corrupted. These walk that gap one term at a time.
                if (stage > 6.5 && stage < 7.5) {
                    float3 i = floor(p);
                    float3 f = fract(p);
                    float n000 = dot(gradient(i), f);
                    float v = clamp((n000 * 0.5) + 0.5, 0.0, 1.0);
                    return half4(v, v, v, 1.0);
                }
                if (stage > 7.5 && stage < 8.5) {
                    float3 i = floor(p);
                    float3 f = fract(p);
                    float n000 = dot(gradient(i + float3(0.0, 0.0, 0.0)), f - float3(0.0, 0.0, 0.0));
                    float n100 = dot(gradient(i + float3(1.0, 0.0, 0.0)), f - float3(1.0, 0.0, 0.0));
                    float n = mix(n000, n100, fade(f.x));
                    float v = clamp((n * 0.5) + 0.5, 0.0, 1.0);
                    return half4(v, v, v, 1.0);
                }
                if (stage > 8.5 && stage < 9.5) {
                    float3 i = floor(p);
                    float3 f = fract(p);
                    float n000 = dot(gradient(i + float3(0.0, 0.0, 0.0)), f - float3(0.0, 0.0, 0.0));
                    float n100 = dot(gradient(i + float3(1.0, 0.0, 0.0)), f - float3(1.0, 0.0, 0.0));
                    float n010 = dot(gradient(i + float3(0.0, 1.0, 0.0)), f - float3(0.0, 1.0, 0.0));
                    float n110 = dot(gradient(i + float3(1.0, 1.0, 0.0)), f - float3(1.0, 1.0, 0.0));
                    float nx00 = mix(n000, n100, fade(f.x));
                    float nx10 = mix(n010, n110, fade(f.x));
                    float n = mix(nx00, nx10, fade(f.y));
                    float v = clamp((n * 0.5) + 0.5, 0.0, 1.0);
                    return half4(v, v, v, 1.0);
                }

                // --- THE FIX --- full noise, identical to stage 0 in
                // every respect except that f is derived from i instead of
                // computed independently via fract(p). Stage 0 corrupted +
                // this clean == diagnosis confirmed. (Stage 11.)
                if (stage > 10.5) {
                    float n = gradientNoise3DFixed(p);
                    float v = clamp((n * 0.5) + 0.5, 0.0, 1.0);
                    return half4(v, v, v, 1.0);
                }

                // --- MAGNITUDE test --- full noise, but with the large
                // per-seed offset stripped so lattice coordinates stay small.
                // The precision probe cleared fp32 at magnitude ~1000 for a
                // SHALLOW expression; this asks whether the DEEP one still
                // holds up there.
                if (stage > 9.5 && stage < 10.5) {
                    float3 small = float3(
                        xscale * (fragCoord.x / resolution.x) + fract(seedOffset.x),
                        yscale * (fragCoord.y / resolution.y) + fract(seedOffset.y),
                        (tscale * time) + fract(seedOffset.z));
                    float n = gradientNoise3D(small);
                    float v = clamp((n * 0.5) + 0.5, 0.0, 1.0);
                    return half4(v, v, v, 1.0);
                }

                float n = gradientNoise3D(p);
                float v = clamp((n * 0.5) + 0.5, 0.0, 1.0);
                return half4(v, v, v, 1.0);
            }
            """;

        private static readonly string[] StageNames =
        [
            "0_final", "1_fracts", "2_lattice", "3_hash", "4_gradient", "5_fade", "6_uniforms",
            "7_one_corner", "8_lerp_x", "9_lerp_xy", "10_full_smallcoords",
            "11_full_FIXED",
        ];

        // Stage 11 lives in its own effect (a shader's main() signature is
        // fixed per-effect), so it is rendered separately — see RunOnce.
        private const string FloatMainStageName = "11_full_float_main";

        /// <summary>
        /// Stage 11: byte-for-byte the same noise maths, but the entry point
        /// returns float4 instead of half4 and nothing in the chain is ever
        /// half. SkSL's `half` is genuinely 16-bit, and Skia's compiler is
        /// free to propagate a relaxed-precision requirement BACKWARD from
        /// the return type into the expression tree feeding it. In a shallow
        /// expression (the stages above, the earlier precision probe) there
        /// is nothing to demote and no visible effect — but the full noise
        /// path is a deep tree of floor/fract on magnitude-1000 coordinates,
        /// exactly the maths that dies at 16 bits, and it is the ONLY stage
        /// that reproduces the corruption. If this renders clean while
        /// stage 0 does not, that is the bug, and the fix is to stop
        /// declaring the entry point as half4.
        /// </summary>
        private const string FloatMainSource = """
            uniform float4 u0;
            uniform float4 u1;
            uniform float4 u2;

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

            float4 main(float2 fragCoord) {
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
                return float4(v, v, v, 1.0);
            }
            """;

        private static SKRuntimeEffect? _effect;
        private static SKRuntimeEffect? _floatMainEffect;
        private static bool _ran;

        /// <summary>
        /// Runs once per process, only when EDITSHARP_SHADER_BISECT names a
        /// folder. `u0`/`u1`/`u2` must be exactly the arrays SkNoiseClip
        /// built for the real draw, so this exercises the real uniforms.
        /// </summary>
        public static void RunOnce(
            int width, int height, SkSurfacePool pool, float[] u0, float[] u1, float[] u2)
        {
            if (_ran) return;

            string? dir = Path.Combine(AppContext.BaseDirectory, "Dump");
            if (string.IsNullOrEmpty(dir)) return;

            _ran = true;

            try
            {
                if (_effect == null)
                {
                    _effect = SKRuntimeEffect.CreateShader(BisectSource, out string errors);
                    if (_effect == null)
                        throw new InvalidOperationException($"bisect shader failed to compile: {errors}");
                }

                Directory.CreateDirectory(dir);

                for (int stage = 0; stage < StageNames.Length; stage++)
                {
                    SKSurface gpuSurface = pool.Rent(width, height);
                    try
                    {
                        DrawStage(gpuSurface.Canvas, width, height, u0, u1, u2, stage);
                        using SKImage gpuImage = gpuSurface.Snapshot();
                        Save(gpuImage, Path.Combine(dir, $"stage{StageNames[stage]}_gpu.png"));
                    }
                    finally
                    {
                        pool.Return(gpuSurface, width, height);
                    }

                    var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                    using SKSurface cpuSurface = SKSurface.Create(info);
                    DrawStage(cpuSurface.Canvas, width, height, u0, u1, u2, stage);
                    using SKImage cpuImage = cpuSurface.Snapshot();
                    Save(cpuImage, Path.Combine(dir, $"stage{StageNames[stage]}_cpu.png"));
                }

                RunFloatMainStage(dir, width, height, pool, u0, u1, u2);

                EditSharpConfig.Logger.LogWarning(
                    $"Noise shader bisect written to '{Path.GetFullPath(dir)}' " +
                    $"({StageNames.Length} stages x gpu/cpu). Uniforms used: " +
                    $"u0=[{string.Join(",", u0)}] u1=[{string.Join(",", u1)}] u2=[{string.Join(",", u2)}]");
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogWarning($"Noise shader bisect failed: {ex.Message}");
            }
        }

        /// <summary>Renders stage 11 — see FloatMainSource's remarks.</summary>
        private static void RunFloatMainStage(
            string dir, int width, int height, SkSurfacePool pool, float[] u0, float[] u1, float[] u2)
        {
            try
            {
                if (_floatMainEffect == null)
                {
                    _floatMainEffect = SKRuntimeEffect.CreateShader(FloatMainSource, out string errors);
                    if (_floatMainEffect == null)
                        throw new InvalidOperationException($"float4-main shader failed to compile: {errors}");
                }

                var uniforms = new SKRuntimeEffectUniforms(_floatMainEffect)
                {
                    ["u0"] = u0,
                    ["u1"] = u1,
                    ["u2"] = u2,
                };

                using SKShader shader = _floatMainEffect.ToShader(uniforms);
                using var paint = new SKPaint { Shader = shader };

                SKSurface gpuSurface = pool.Rent(width, height);
                try
                {
                    gpuSurface.Canvas.Clear(SKColors.Black);
                    gpuSurface.Canvas.DrawRect(new SKRect(0, 0, width, height), paint);
                    using SKImage gpuImage = gpuSurface.Snapshot();
                    Save(gpuImage, Path.Combine(dir, $"stage{FloatMainStageName}_gpu.png"));
                }
                finally
                {
                    pool.Return(gpuSurface, width, height);
                }

                var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                using SKSurface cpuSurface = SKSurface.Create(info);
                cpuSurface.Canvas.Clear(SKColors.Black);
                cpuSurface.Canvas.DrawRect(new SKRect(0, 0, width, height), paint);
                using SKImage cpuImage = cpuSurface.Snapshot();
                Save(cpuImage, Path.Combine(dir, $"stage{FloatMainStageName}_cpu.png"));
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogWarning($"float4-main bisect stage failed: {ex.Message}");
            }
        }

        private static void DrawStage(
            SKCanvas canvas, int width, int height, float[] u0, float[] u1, float[] u2, int stage)
        {
            var uniforms = new SKRuntimeEffectUniforms(_effect!)
            {
                ["u0"] = u0,
                ["u1"] = u1,
                ["u2"] = u2,
                ["u3"] = new float[] { stage, 0f, 0f, 0f },
            };

            using SKShader shader = _effect!.ToShader(uniforms);
            using var paint = new SKPaint { Shader = shader };

            canvas.Clear(SKColors.Black);
            canvas.DrawRect(new SKRect(0, 0, width, height), paint);
        }

        private static void Save(SKImage image, string path)
        {
            using SKData? png = image.Encode(SKEncodedImageFormat.Png, 100);
            if (png != null) File.WriteAllBytes(path, png.ToArray());
        }
    }
}