using System;
using SkiaSharp;
using EditSharp.Components;

namespace EditSharp.Components.Clips
{
    /// <summary>
    /// Item 10, decided in conversation: eliminate the pre-render pass
    /// entirely rather than port it. NoiseRenderer/OptimizedMediaBuilder's
    /// noise path existed to solve exactly one problem — `perlin` is a
    /// sequential-only ffmpeg generator source with no seek, so asking for
    /// output frame N made ffmpeg generate and discard every frame before
    /// it (confirmed O(N) per frame, O(N^2) across a render). But
    /// NoiseRenderer's OWN BuildPerlinSource already shows the underlying
    /// noise function has no such constraint: x = xscale*column/width,
    /// y = yscale*row/height, t = tscale*seconds — a PURE function of
    /// (pixel position, time), zero cross-frame state. The O(N) problem was
    /// never inherent to Perlin noise, only to ffmpeg's CLI generator model
    /// not exposing random access to it. A real-time shader has true random
    /// access by construction — there is no "frame N" to have generated
    /// frames 0..N-1 first for. No pre-render, no FFV1 intermediate file,
    /// no seek, nothing to pre-render AT ALL.
    ///
    /// This is also a genuinely good SKRuntimeEffect (SkSL) use case, in
    /// contrast to the geometric transitions (item 8) — this is real
    /// per-pixel procedural computation with no clip-region equivalent, the
    /// exact case shaders are the right tool for.
    ///
    /// HONESTY FLAG, not silently glossed over: this is a STANDARD
    /// gradient-noise (Perlin-style) implementation — same class of
    /// coherent noise, same general look — NOT a byte-exact port of
    /// ffmpeg's own `perlin` filter algorithm. ffmpeg's specific gradient
    /// hash / permutation-table derivation from a seed isn't something I
    /// have reliable enough recall of to reproduce bit-for-bit, and this
    /// shader uses a different (hash-based, no permutation table) gradient
    /// scheme. Existing content using NoiseClip WILL look visually
    /// different after this migration, not pixel-identical. Flagged in the
    /// migration manifest's deferred-verification list, same treatment as
    /// item 8's FadeToColor/other honesty flags.
    ///
    /// Detail/SeetheRate normalization constants (DetailCellsPerCanvas,
    /// SeetheCellsPerSecond) are carried over unchanged from
    /// NoiseRenderer.cs so existing Detail/SeetheRate slider VALUES still
    /// mean roughly the same thing (same coordinate scale), even though the
    /// resulting texture itself isn't bit-identical.
    ///
    /// Renders directly at canvasWidth x canvasHeight, matching the old
    /// code's own convention (FrameFilterChain's NoiseClip case set
    /// nativeWidth/nativeHeight = canvasWidth/canvasHeight the same way
    /// GeneratorClip did) — noise genuinely needs real per-pixel variation
    /// unlike a flat colour, so there's no equivalent to GeneratorClip's
    /// 1x1 shortcut here; the standard content-resize step downstream
    /// handles any further scaling from this canvas-sized source, same as
    /// it always did.
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

            // Classic-structure gradient (Perlin-style) 3D noise: 8 corner
            // gradients, dot with offset, fade-curve interpolation. NOT
            // ffmpeg's own permutation-table algorithm — see class remarks.
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
                // Independent offset per axis (not one scalar reused for
                // all three) — a shared offset would correlate x/y/t in a
                // way that risks a faint diagonal-streak artifact; three
                // unrelated offsets avoid that for negligible extra cost.
                float x = xscale * (fragCoord.x / resolution.x) + seedOffset.x;
                float y = yscale * (fragCoord.y / resolution.y) + seedOffset.y;
                float t = (tscale * time) + seedOffset.z;

                float n = gradientNoise3D(float3(x, y, t));

                // Raw output is roughly [-0.87, 0.87] (dot of a unit-ish
                // gradient with a unit-cube diagonal) — remap to [0, 1] and
                // clamp. Contrast/range not yet visually tuned against real
                // output; deferred to the same real-render check as
                // everything else in this migration.
                float v = clamp((n * 0.5) + 0.5, 0.0, 1.0);

                return half4(v, v, v, 1.0);
            }
            """;

        private static readonly SKRuntimeEffect Effect = CreateEffect();

        private static SKRuntimeEffect CreateEffect()
        {
            SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(ShaderSource, out string errors);
            if (effect == null)
                throw new InvalidOperationException($"NoiseClip shader failed to compile: {errors}");
            return effect;
        }

        /// <summary>
        /// Renders this clip's noise at a specific clip-relative instant,
        /// directly at canvasWidth x canvasHeight — no pre-render, no seek,
        /// no dependency on any other frame. Every frame is O(1) regardless
        /// of frame index, which is the entire point of this item.
        /// </summary>
        public static SKImage Render(NoiseClip clip, double clipSeconds, int canvasWidth, int canvasHeight)
        {
            double xscale = Math.Max(clip.Detail, 0f) * DetailCellsPerCanvas;
            double yscale = xscale * canvasHeight / (double)canvasWidth;
            double tscale = Math.Max(clip.SeetheRate, 0f) * SeetheCellsPerSecond;

            // Simple seed variation: offset the sampled coordinate space by
            // three independent, seed-derived amounts (one per axis — see
            // the shader's own comment on why not one shared scalar).
            // Different seeds land in unrelated-looking regions of the same
            // underlying noise field — cheap and effective for a hash-based
            // field with no permutation table to reseed directly. Not
            // equivalent to ffmpeg's own random_seed handling (see class
            // remarks). Not stress-tested across many seed values —
            // flagged as deferred verification, not assumed robust.
            var rng = new Random(clip.Seed);
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

            using SKSurface surface = SKSurface.Create(
                new SKImageInfo(canvasWidth, canvasHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
            surface.Canvas.DrawRect(new SKRect(0, 0, canvasWidth, canvasHeight), paint);

            return surface.Snapshot();
        }
    }
}
