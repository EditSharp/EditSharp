using System;
using System.IO;
using SkiaSharp;

namespace EditSharp.Composite
{
    /// <summary>
    /// TEMPORARY DIAGNOSTIC — measures the EFFECTIVE floating-point
    /// precision of SkSL shader execution on whatever backend is actually
    /// running, by rendering the same probe twice: once through the real
    /// GPU surface pool, once on a plain CPU raster surface (which is
    /// always true fp32 and therefore a known-good control).
    ///
    /// WHY THIS EXISTS. The block-corruption bug in SkNoiseClip is now
    /// confirmed DETERMINISTIC (bit-identical corruption across separate
    /// runs), which rules out uninitialized VRAM, races, and command
    /// submission ordering — every one of those produces VARYING garbage.
    /// What's left is something computed wrong the same way every time.
    /// The prime candidate is precision: SkNoiseClip adds a per-seed offset
    /// of up to 1000.0 to normalized coordinates before running them
    /// through floor()/fract()/hash math, while adjacent pixels differ by
    /// only ~0.016. That ratio is fine at fp32, but at fp16 the ULP at
    /// magnitude 1024+ is 1.0 — dozens of neighbouring pixels quantize onto
    /// the exact same lattice cell, which renders as uniform rectangular
    /// blocks. Deterministic, present from frame one, GPU-only, and
    /// completely invisible to the D3D12 validation layer (it is not an API
    /// error at all) — matching every observation on record.
    ///
    /// WHAT THE PROBE DRAWS. A 30-cycle sawtooth ramp across X, twice:
    ///   TOP HALF    — fract(u * 30 + 1000.0)  (precision-stressed: the
    ///                 same "small delta riding on a big base" pattern the
    ///                 noise shader uses)
    ///   BOTTOM HALF — fract(u * 30)           (control: identical maths
    ///                 with no large offset)
    /// At full fp32 both halves are smooth, visually identical ramps. If
    /// precision is being reduced, the TOP half breaks into visible flat
    /// steps/blocks while the BOTTOM half stays smooth — and the width of
    /// those steps directly reveals how many bits of mantissa were actually
    /// available. A GPU/CPU difference proves the demotion is happening in
    /// GPU shader execution specifically, not in the maths itself.
    /// </summary>
    internal static class SkShaderPrecisionProbe
    {
        private const string ProbeSource = """
            uniform float4 p0; // width, height, bigOffset, unused

            half4 main(float2 fragCoord) {
                float u = fragCoord.x / p0.x;
                float y = fragCoord.y / p0.y;

                float stressed = fract((u * 30.0) + p0.z);
                float control  = fract(u * 30.0);

                float v = y < 0.5 ? stressed : control;
                return half4(v, v, v, 1.0);
            }
            """;

        private static SKRuntimeEffect? _effect;
        private static bool _ran;

        /// <summary>
        /// Runs once per process, only when EDITSHARP_SHADER_PRECISION_TEST
        /// names a folder. Writes precision_gpu.png and precision_cpu.png.
        /// </summary>
        public static void RunOnce(int width, int height, SkSurfacePool pool)
        {
            if (_ran) return;

            string? dir = Path.Combine(AppContext.BaseDirectory, "Dump");
            if (string.IsNullOrEmpty(dir)) return;

            _ran = true;

            try
            {
                if (_effect == null)
                {
                    _effect = SKRuntimeEffect.CreateShader(ProbeSource, out string errors);
                    if (_effect == null)
                        throw new InvalidOperationException($"probe shader failed to compile: {errors}");
                }

                Directory.CreateDirectory(dir);

                // GPU — through the very same pool the noise shader draws to.
                SKSurface gpuSurface = pool.Rent(width, height);
                try
                {
                    DrawProbe(gpuSurface.Canvas, width, height);
                    using SKImage gpuImage = gpuSurface.Snapshot();
                    Save(gpuImage, Path.Combine(dir, "precision_gpu.png"));
                }
                finally
                {
                    pool.Return(gpuSurface, width, height);
                }

                // CPU raster control — always true fp32.
                var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                using (SKSurface cpuSurface = SKSurface.Create(info))
                {
                    DrawProbe(cpuSurface.Canvas, width, height);
                    using SKImage cpuImage = cpuSurface.Snapshot();
                    Save(cpuImage, Path.Combine(dir, "precision_cpu.png"));
                }

                EditSharpConfig.Logger.LogWarning(
                    $"Shader precision probe written to '{Path.GetFullPath(dir)}' " +
                    "(precision_gpu.png / precision_cpu.png).");
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogWarning($"Shader precision probe failed: {ex.Message}");
            }
        }

        private static void DrawProbe(SKCanvas canvas, int width, int height)
        {
            var uniforms = new SKRuntimeEffectUniforms(_effect!)
            {
                ["p0"] = new float[] { width, height, 1000f, 0f },
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