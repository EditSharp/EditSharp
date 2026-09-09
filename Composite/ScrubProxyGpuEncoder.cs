using System;
using SkiaSharp;

namespace EditSharp.Composite
{
    /// <summary>
    /// GPU encode for ScrubProxyPixelFormat.IndexedDelta7 — replacing
    /// IndexedDelta7Codec.EncodeWithPalette's CPU-side strictly-sequential-
    /// per-row search with a GPU pipeline that does the same search with
    /// real parallelism. DIRECT RESPONSE TO THE USER'S EXPLICIT REQUEST:
    /// the proxy files ScrubProxyCache writes are already shaped to be fast
    /// to DECODE (that was the whole point of IndexedDelta7 — see
    /// IndexedDelta7Codec's own class remarks), but nothing about that
    /// shape makes them any faster to PRODUCE in the first place. This file
    /// is what speeds up the BUILD side — the thing that actually needed
    /// to get faster.
    ///
    /// WHY ENCODE CANNOT USE A LOGARITHMIC PARALLEL-SCAN TRICK THE WAY A
    /// DECODE PASS CAN, STATED UP FRONT SO THE LINEAR PIPELINE SHAPE BELOW
    /// DOESN'T LOOK LIKE AN OVERSIGHT: decoding IndexedDelta7's control
    /// bytes back to pixels is an associative "clamp-affine" composition
    /// (clamp(prev + offset, 0, 255)), which is what makes an O(log width)
    /// parallel scan mathematically valid for THAT direction. ENCODING has
    /// no such structure: choosing a control byte at pixel x is an ARGMIN
    /// over 128 palette-vs-delta candidates (IndexedDelta7Codec.EncodeRows's
    /// own FindBestDelta), and that argmin is not affine, not associative,
    /// and depends on the TRUE reconstructed color of pixel x-1 — which is
    /// itself the output of the same non-affine decision made at x-1.
    /// There is no way to "combine two decisions into one decision of the
    /// same shape" the way decode's math allows; the dependency chain has
    /// to actually be walked, one column at a time, in order. What GPU
    /// parallelism CAN still buy here — and does — is running every ROW's
    /// walk SIMULTANEOUSLY (rows are fully independent of each other — see
    /// IndexedDelta7Codec's own ROW-START BOOTSTRAP remarks: column 0 of
    /// every row is unconditionally PALETTE mode, so no row ever depends on
    /// any other row's content), plus running the embarrassingly-parallel
    /// per-pixel nearest-PALETTE search (which does NOT depend on any
    /// left-neighbor at all) as its own separate, fully parallel prepass.
    ///
    /// PIPELINE SHAPE — TWO STAGES:
    ///   1. NEAREST-PALETTE PREPASS (ONE fully parallel draw over the whole
    ///      frame): for every pixel independently, finds its nearest of the
    ///      128 shared-palette entries (the exact same 128-entry search
    ///      IndexedDelta7Codec.FindNearestPaletteIndex does, and the exact
    ///      same DistanceSquared metric) and stores (index, R, G, B) into a
    ///      "candidate" data texture. This has ZERO row/column dependency —
    ///      it's the GPU equivalent of the CPU loop's `nearestPaletteCache`,
    ///      just computed directly per pixel instead of cached per distinct
    ///      color (a GPU fragment shader has no equivalent of a
    ///      Dictionary&lt;uint,...&gt; cache across invocations, so this
    ///      prepass simply always does the full 128-entry search — cheap
    ///      next to the delta search below, and embarrassingly parallel).
    ///   2. COLUMN-BY-COLUMN DELTA SCAN (`width` sequential draws, each over
    ///      the WHOLE frame): pass `x` computes the FINAL (code, chosenR,
    ///      chosenG, chosenB) for every pixel in column `x`, for EVERY row
    ///      AT ONCE (that's the actual parallelism win — up to `height`
    ///      independent row-walks advancing one step per pass, instead of
    ///      one CPU thread advancing one row at a time through the whole
    ///      frame) — reading column x-1's ALREADY-FINALIZED state from the
    ///      previous pass's output texture for the real left-neighbor
    ///      dependency, and copying every other column's value through
    ///      UNCHANGED from the previous pass (a cheap texture fetch, not a
    ///      re-computation) so the ping-ponged "state" texture always holds
    ///      every column finalized so far. After `width` passes, the state
    ///      texture holds the CORRECT final (code, R, G, B) for every
    ///      pixel in the frame — only the CODE channel is what's actually
    ///      read back and written to disk; the RGB channels only ever exist
    ///      to feed the NEXT pass's left-neighbor lookup.
    ///
    /// THIS IS A LINEAR (O(width)) SCAN, NOT A LOGARITHMIC ONE, AND THAT'S
    /// EXPECTED, NOT A MISSED OPTIMIZATION: see WHY ENCODE CANNOT USE A
    /// LOGARITHMIC PARALLEL-SCAN TRICK above — there is no associative
    /// combine here to double the stride with each pass, so every one of
    /// the `width` columns needs its own pass. What this pipeline buys
    /// over the CPU path is not fewer total per-pixel decisions (it's
    /// exactly the same width*height decisions, same 128+128-candidate
    /// search per non-cached pixel) — it's that each pass's `height` many
    /// decisions (one per row, at that pass's column) run CONCURRENTLY on
    /// the GPU instead of sequentially on one CPU thread, and that the
    /// nearest-palette search (stage 1) runs fully in parallel across every
    /// pixel in the frame at once rather than needing its own sequential
    /// pass at all.
    ///
    /// DRAW-CALL-COUNT RISK, HONESTLY FLAGGED, NOT YET MEASURED: this
    /// pipeline issues 1 + `width` draw calls PER FRAME (e.g. 385 for a
    /// 384-wide proxy), each one small (a handful of GPU-microseconds of
    /// real work per pass once the nearest-palette prepass has done the
    /// expensive part), and a scrub proxy build encodes potentially
    /// hundreds of frames. If CPU-side draw/command-submission overhead per
    /// call turns out to dominate wall time (plausible — these are far
    /// smaller draws than anything else in this codebase's GPU paths), the
    /// per-pass full-frame draw (see PIPELINE SHAPE) is the first thing to
    /// revisit: batching multiple frames' rows into one wider atlas texture
    /// so one column-pass advances many frames at once, or restricting each
    /// pass's draw to a 1-column-wide scissor rect instead of a full-frame
    /// redraw, are both real follow-up levers — NEITHER is implemented
    /// here; this is a first, correctness-first pass. Compare this path's
    /// output against IndexedDelta7Codec.EncodeWithPalette's own CPU output
    /// on real captured frames, AND time both, before trusting this path in
    /// production or drawing any conclusion about whether it's actually a
    /// net win at the frame counts/resolutions real scrub proxies use.
    ///
    /// NO fp16-style OFFSET-SCALING TRICK NEEDED HERE: this pipeline never
    /// accumulates anything by repeated addition — every pass computes a
    /// chosen R/G/B fresh via ONE clamp(prev + level*step) per channel, and
    /// every stored code/R/G/B value is an exact integer 0-255 (or 0-127
    /// for a palette index), well inside the range BOTH half and float can
    /// represent exactly. The one place this file DOES take a defensive
    /// posture is doing every actual distance/arithmetic computation in
    /// explicit `float` locals (never relying on `half`-typed
    /// intermediates) before narrowing back to `half4` only at each
    /// shader's final return — the redmean-style DistanceSquared metric's
    /// products (weight up to ~767, squared 8-bit deltas up to 65025) can
    /// reach the tens of millions, which OVERFLOWS `half`'s ~65504 finite
    /// range outright, not just loses precision — an actual correctness
    /// hazard if left in `half`.
    ///
    /// EVERY SkiaSharp API SURFACE BELOW IS WRITTEN FROM FAMILIARITY WITH
    /// THIS CODEBASE'S EXISTING SKRuntimeEffect/SkSurfacePool USAGE, NOT
    /// COMPILE-TESTED IN THIS ENVIRONMENT. TryEncode wraps its entire body
    /// in one try/catch and returns false on ANY failure — a caller MUST
    /// fall back to IndexedDelta7Codec.EncodeWithPalette when this returns
    /// false. This encoder is explicitly NOT the only way to get a correct
    /// proxy build; it's an opportunistic accelerator, never the only path.
    ///
    /// MUST BE CALLED ON THE GPU-OWNING THREAD (see GpuThreadDispatcher's
    /// class remarks, GPU WORK MUST STAY ON ONE THREAD). ScrubProxyCache is
    /// responsible for calling this only via its own dedicated encode-side
    /// GpuThreadDispatcher.
    /// </summary>
    internal static class ScrubProxyGpuEncoder
    {
        private static readonly SKSamplingOptions SamplingNearest =
            new(SKFilterMode.Nearest, SKMipmapMode.None);

        // STAGE 1 — nearest-palette prepass. Fully parallel, no left-
        // neighbor dependency at all: every pixel independently finds its
        // closest of the 128 shared-palette entries under the same
        // redmean-style metric IndexedDelta7Codec.DistanceSquared uses.
        // Output: (index, R, G, B) as raw 0-255 (0-127 for index) floats
        // into an RgbaF32 data surface — see class remarks, NO OFFSET-
        // SCALING TRICK NEEDED HERE.
        private const string NearestPaletteShaderSource = """
            uniform shader sourceTex;   // the raw RGBA8888 proxy-resolution source frame
            uniform shader paletteTex;  // Rgba8888, 128x1: the shared IndexedDelta7 palette

            half4 main(float2 fragCoord) {
                half4 src = sourceTex.eval(fragCoord);
                float r = floor(float(src.r) * 255.0 + 0.5);
                float g = floor(float(src.g) * 255.0 + 0.5);
                float b = floor(float(src.b) * 255.0 + 0.5);

                float bestIndex = 0.0;
                float bestR = 0.0;
                float bestG = 0.0;
                float bestB = 0.0;
                float bestDistance = 3.0e38;

                for (int i = 0; i < 128; i++) {
                    half4 p = paletteTex.eval(float2(float(i) + 0.5, 0.5));
                    float pr = floor(float(p.r) * 255.0 + 0.5);
                    float pg = floor(float(p.g) * 255.0 + 0.5);
                    float pb = floor(float(p.b) * 255.0 + 0.5);

                    float rMean = floor((r + pr) / 2.0);
                    float dr = r - pr;
                    float dg = g - pg;
                    float db = b - pb;
                    float dist = (512.0 + rMean) * dr * dr + 1024.0 * dg * dg + (767.0 - rMean) * db * db;

                    if (dist < bestDistance) {
                        bestDistance = dist;
                        bestIndex = float(i);
                        bestR = pr; bestG = pg; bestB = pb;
                    }
                }

                return half4(bestIndex, bestR, bestG, bestB);
            }
            """;

        // STAGE 2 — one column's worth of the real PALETTE-vs-DELTA
        // decision, for every row at once. `column` selects which pixel
        // column this pass actually computes fresh; every other column is
        // copied through UNCHANGED from `prevTex` (the previous pass's own
        // output) so the ping-ponged state texture always holds every
        // column finalized so far. See class remarks, PIPELINE SHAPE.
        private const string EncodeColumnShaderSource = """
            uniform shader sourceTex;     // the raw RGBA8888 proxy-resolution source frame
            uniform shader candidateTex;  // stage 1 output: (index, R, G, B) per pixel
            uniform shader prevTex;       // previous pass's (code, chosenR, chosenG, chosenB) per pixel
            uniform float column;         // 0..width-1: which column this pass finalizes

            half4 main(float2 fragCoord) {
                // Not this pass's column yet (or already finalized by an
                // earlier pass) — carry the existing state through
                // unchanged. Compared with a tolerance, not equality (exact
                // for the whole-number values this uniform ever takes, but
                // tolerant of an unexpected sub-ULP nudge).
                if (abs(fragCoord.x - column) > 0.5) {
                    return prevTex.eval(fragCoord);
                }

                half4 src = sourceTex.eval(fragCoord);
                float r = floor(float(src.r) * 255.0 + 0.5);
                float g = floor(float(src.g) * 255.0 + 0.5);
                float b = floor(float(src.b) * 255.0 + 0.5);

                half4 cand = candidateTex.eval(fragCoord);
                float paletteIdx = float(cand.r);
                float palR = float(cand.g);
                float palG = float(cand.b);
                float palB = float(cand.a);

                float rMeanP = floor((r + palR) / 2.0);
                float drp = r - palR;
                float dgp = g - palG;
                float dbp = b - palB;
                float paletteDistance =
                    (512.0 + rMeanP) * drp * drp + 1024.0 * dgp * dgp + (767.0 - rMeanP) * dbp * dbp;

                // See IndexedDelta7Codec's own ROW-START BOOTSTRAP remarks
                // — column 0 of every row is unconditionally PALETTE mode,
                // no left-neighbor lookup is even well-defined yet.
                if (column < 0.5) {
                    return half4(paletteIdx, palR, palG, palB);
                }

                half4 prevState = prevTex.eval(float2(column - 1.0, fragCoord.y));
                float prevR = float(prevState.g);
                float prevG = float(prevState.b);
                float prevB = float(prevState.a);

                // Exhaustive 4*8*4=128-candidate delta search — same
                // shape, same step sizes, same bit-field layout as
                // IndexedDelta7Codec.FindBestDelta/ApplyDelta. A fully
                // static loop bound (never depends on a uniform), so this
                // compiles the same way regardless of frame size.
                float bestCode = 0.0;
                float bestR = prevR;
                float bestG = prevG;
                float bestB = prevB;
                float bestDeltaDistance = 3.0e38;

                for (int rl = 0; rl < 4; rl++) {
                    for (int gl = 0; gl < 8; gl++) {
                        for (int bl = 0; bl < 4; bl++) {
                            float candR = clamp(prevR + (float(rl) - 2.0) * 6.0, 0.0, 255.0);
                            float candG = clamp(prevG + (float(gl) - 4.0) * 4.0, 0.0, 255.0);
                            float candB = clamp(prevB + (float(bl) - 2.0) * 6.0, 0.0, 255.0);

                            float rMean = floor((r + candR) / 2.0);
                            float dr = r - candR;
                            float dg = g - candG;
                            float db = b - candB;
                            float dist = (512.0 + rMean) * dr * dr + 1024.0 * dg * dg + (767.0 - rMean) * db * db;

                            if (dist < bestDeltaDistance) {
                                bestDeltaDistance = dist;
                                bestCode = 128.0 + float(rl) * 32.0 + float(gl) * 4.0 + float(bl);
                                bestR = candR; bestG = candG; bestB = candB;
                            }
                        }
                    }
                }

                // Whichever candidate is actually closer to the real
                // source pixel wins — same tie-break (palette wins ties)
                // as IndexedDelta7Codec.EncodeRows.
                if (paletteDistance <= bestDeltaDistance) {
                    return half4(paletteIdx, palR, palG, palB);
                }
                return half4(bestCode, bestR, bestG, bestB);
            }
            """;

        private static readonly SKRuntimeEffect NearestPaletteEffect =
            CreateEffect(NearestPaletteShaderSource, "nearestPalette");
        private static readonly SKRuntimeEffect EncodeColumnEffect =
            CreateEffect(EncodeColumnShaderSource, "encodeColumn");

        private static SKRuntimeEffect CreateEffect(string source, string name)
        {
            SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(source, out string errors);
            if (effect == null)
                throw new InvalidOperationException(
                    $"ScrubProxyGpuEncoder's {name} shader failed to compile: {errors}");
            return effect;
        }

        /// <summary>
        /// Attempts a full GPU encode of one IndexedDelta7 frame against
        /// `palette` (the file's shared/global palette — exactly
        /// Delta7PaletteByteSize bytes, same shape
        /// IndexedDelta7Codec.EncodeWithPalette itself takes). `frame` is
        /// the already-decoded, already-proxy-resolution RGBA8888 source
        /// frame (the same SKImage ScrubProxyCache.EncodeFramePixels reads
        /// via PeekPixels for the CPU path). `pixelsOut` receives exactly
        /// width*height control bytes, in the SAME row-major layout
        /// IndexedDelta7Codec.EncodeWithPalette/Decode both use.
        ///
        /// Returns false on ANY failure, for ANY reason — see class
        /// remarks. The caller MUST fall back to
        /// IndexedDelta7Codec.EncodeWithPalette when this returns false.
        ///
        /// MUST be called already running on the GPU-owning thread (see
        /// class remarks) — this method does no thread marshaling of its
        /// own.
        /// </summary>
        public static bool TryEncode(
            GpuContext gpuContext, SkSurfacePool pool,
            SKImage frame, ReadOnlySpan<byte> palette,
            int width, int height, Span<byte> pixelsOut)
        {
            if (gpuContext.GRContext == null) return false;
            if (width <= 0 || height <= 0) return false;
            if (palette.Length != ScrubProxyFormat.Delta7PaletteByteSize) return false;
            if (pixelsOut.Length != width * height) return false;

            try
            {
                using SKImage paletteImage = BuildPaletteImage(palette);

                using SKShader sourceShader =
                    frame.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, SamplingNearest);
                using SKShader paletteShader =
                    paletteImage.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, SamplingNearest);

                using SKImage candidateImage = RunNearestPalettePrepass(pool, sourceShader, paletteShader, width, height);
                using SKShader candidateShader =
                    candidateImage.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, SamplingNearest);

                using SKImage finalState = RunColumnScan(pool, sourceShader, candidateShader, width, height);

                return ReadBackCodes(finalState, width, height, pixelsOut);
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogWarning(
                    "ScrubProxyGpuEncoder: GPU IndexedDelta7 encode failed, falling back to the proven CPU " +
                    $"path (IndexedDelta7Codec.EncodeWithPalette): {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Uploads the shared palette as a 128x1 Rgba8888 SKImage. Kept as
        /// its own copy rather than a shared helper for the same reason
        /// IndexedDelta7Codec's own BuildPalette is kept independent of
        /// other quantizers — this file is meant to be independently
        /// understandable and independently safe to fall back away from.
        /// </summary>
        private static SKImage BuildPaletteImage(ReadOnlySpan<byte> palette)
        {
            const int count = ScrubProxyFormat.Delta7PaletteEntryCount;
            var info = new SKImageInfo(count, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var bitmap = new SKBitmap(info);

            Span<byte> pixels = bitmap.GetPixelSpan();
            for (int i = 0; i < count; i++)
            {
                int dst = i * 4;
                int src = i * 3;
                pixels[dst] = palette[src];
                pixels[dst + 1] = palette[src + 1];
                pixels[dst + 2] = palette[src + 2];
                pixels[dst + 3] = 255;
            }

            return SKImage.FromBitmap(bitmap);
        }

        private static SKImage RunNearestPalettePrepass(
            SkSurfacePool pool, SKShader sourceShader, SKShader paletteShader, int width, int height)
        {
            var children = new SKRuntimeEffectChildren(NearestPaletteEffect)
            {
                ["sourceTex"] = sourceShader,
                ["paletteTex"] = paletteShader,
            };
            var uniforms = new SKRuntimeEffectUniforms(NearestPaletteEffect);

            using SKShader shader = NearestPaletteEffect.ToShader(uniforms, children);
            return DrawToDataSurface(pool, shader, width, height);
        }

        /// <summary>
        /// The `width`-pass column scan itself — see class remarks,
        /// PIPELINE SHAPE and THIS IS A LINEAR SCAN, NOT A LOGARITHMIC ONE.
        /// Ping-pongs a single state-image handle `width` times, with a
        /// real per-pixel decision computed at each pass.
        /// </summary>
        private static SKImage RunColumnScan(
            SkSurfacePool pool, SKShader sourceShader, SKShader candidateShader, int width, int height)
        {
            // Pass 0 (column 0) never actually reads `current` — every
            // pixel is either "not my column yet" (falls through to
            // prevTex.eval, which is fine to be whatever this seed image
            // holds since it's about to be fully overwritten column-by-
            // column across the remaining passes) or column 0 itself
            // (unconditionally PALETTE mode, no prevTex read at all — see
            // the shader's own ROW-START BOOTSTRAP branch). A freshly
            // rented, uncleared data surface is exactly as safe to seed
            // this with as an explicitly zeroed one would be.
            SKImage current = DrawToDataSurface(
                pool, BuildColumnShader(sourceShader, candidateShader, MakeFlatShader(pool, width, height), 0f, width, height),
                width, height);

            for (int column = 1; column < width; column++)
            {
                using SKShader prevShader =
                    current.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, SamplingNearest);
                SKImage next = DrawToDataSurface(
                    pool, BuildColumnShader(sourceShader, candidateShader, prevShader, (float)column, width, height),
                    width, height);
                current.Dispose();
                current = next;
            }

            return current;
        }

        /// <summary>
        /// A throwaway all-zero data image, used ONLY as pass 0's
        /// `prevTex` — see RunColumnScan's own remarks on why pass 0 never
        /// actually reads meaningful data out of it. Rented, cleared, and
        /// returned immediately rather than kept alive, since nothing
        /// after pass 0 ever touches it again.
        /// </summary>
        private static SKShader MakeFlatShader(SkSurfacePool pool, int width, int height)
        {
            SKSurface surface = pool.RentData(width, height, SKColorType.RgbaF32);
            try
            {
                surface.Canvas.Clear(SKColors.Transparent);
                using SKImage image = surface.Snapshot();
                return image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, SamplingNearest);
            }
            finally
            {
                pool.ReturnData(surface, width, height, SKColorType.RgbaF32);
            }
        }

        private static SKShader BuildColumnShader(
            SKShader sourceShader, SKShader candidateShader, SKShader prevShader, float column, int width, int height)
        {
            var children = new SKRuntimeEffectChildren(EncodeColumnEffect)
            {
                ["sourceTex"] = sourceShader,
                ["candidateTex"] = candidateShader,
                ["prevTex"] = prevShader,
            };
            var uniforms = new SKRuntimeEffectUniforms(EncodeColumnEffect)
            {
                // The shader declares `uniform float column;` — a scalar,
                // not an array — so this must be set as a bare float. An
                // earlier version wrapped it as `new[] { column }`, which
                // SkiaSharp's uniform setter treats as a FloatArray value
                // and rejects against a Float-typed uniform ("Unable to
                // write a 'FloatArray' value to a 'Float' uniform"),
                // failing on literally every frame.
                ["column"] = column,
            };

            return EncodeColumnEffect.ToShader(uniforms, children);
        }

        /// <summary>
        /// Draws `shader` into a freshly-rented RGBA32F DATA surface, ALWAYS
        /// with SKBlendMode.Src: these surfaces carry arbitrary (code, R,
        /// G, B) float payloads, not a color+coverage pair, and a default
        /// SrcOver blend would silently corrupt or discard every draw
        /// (every shader above returns A in the 0-255 range, never a real
        /// 0-1 coverage value).
        /// </summary>
        private static SKImage DrawToDataSurface(SkSurfacePool pool, SKShader shader, int width, int height)
        {
            SKSurface surface = pool.RentData(width, height, SKColorType.RgbaF32);
            try
            {
                using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
                surface.Canvas.DrawRect(new SKRect(0, 0, width, height), paint);
                return surface.Snapshot();
            }
            finally
            {
                pool.ReturnData(surface, width, height, SKColorType.RgbaF32);
            }
        }

        /// <summary>
        /// Reads the finished (code, R, G, B) state image back to the CPU
        /// and extracts just the code channel into `pixelsOut`, row-major,
        /// exactly matching IndexedDelta7Codec's own `pixelsOut[y*width+x]`
        /// layout. This is the one mandatory GPU-to-CPU readback this
        /// whole pipeline pays — everything before this point stayed on
        /// the GPU. Values are rounded to the nearest integer and clamped
        /// to 0-255 defensively before the narrowing cast to byte, in case
        /// of any stray floating-point drift (none is expected — see class
        /// remarks, NO OFFSET-SCALING TRICK NEEDED HERE — but a silently
        /// wrong control byte is a much worse failure mode than a defensive
        /// clamp here).
        /// </summary>
        private static bool ReadBackCodes(SKImage finalState, int width, int height, Span<byte> pixelsOut)
        {
            var dstInfo = new SKImageInfo(width, height, SKColorType.RgbaF32, SKAlphaType.Unpremul);
            byte[] raw = new byte[width * height * 4 * sizeof(float)];

            unsafe
            {
                fixed (byte* rawPtr = raw)
                {
                    if (!finalState.ReadPixels(dstInfo, (IntPtr)rawPtr, width * 4 * sizeof(float), 0, 0))
                        return false;
                }
            }

            Span<float> floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(raw);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int floatOffset = (y * width + x) * 4; // RGBA32F: R holds the code
                    float code = floats[floatOffset];
                    int rounded = (int)MathF.Round(code, MidpointRounding.AwayFromZero);
                    pixelsOut[y * width + x] = (byte)Math.Clamp(rounded, 0, 255);
                }
            }

            return true;
        }
    }
}