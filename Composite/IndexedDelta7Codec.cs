using System;
using System.Collections.Generic;

namespace EditSharp.Composite
{
    /// <summary>
    /// Encoder/decoder for ScrubProxyPixelFormat.IndexedDelta7 — DIRECT
    /// IMPLEMENTATION OF A USER-PROPOSED PSEUDOCODE DESIGN: every pixel is
    /// stored as ONE control byte that is EITHER "the nearest color in this
    /// frame's own small palette" OR "a small modulation of the pixel
    /// immediately to my left," whichever one lands closer to the real
    /// source color — chosen per-pixel, not per-frame.
    ///
    /// WHY THIS EXISTS, BEYOND Indexed8 (the size-down/quality-up case the
    /// user asked for): Indexed8's ordered (Bayer) dithering exists purely
    /// to fake extra perceived color depth out of a fixed 256-color
    /// palette by deliberately varying neighboring pixels' chosen index —
    /// which is exactly what makes a dithered index plane compress poorly
    /// under PackBits RLE (see ScrubProxyRle's own remarks on the
    /// production bug a pathological, dither-like repeating pattern
    /// caused). This format removes the need for dithering ENTIRELY: a
    /// pixel that's close to, but not exactly, a palette color no longer
    /// needs a faked-in neighboring variation to read as smooth — it can
    /// instead land on an exact small delta from its own already-chosen
    /// left neighbor, which (a) is visually more accurate than a dithered
    /// approximation and (b) is dramatically more RLE-friendly, since a
    /// flat OR smoothly-gradient region now tends to produce long runs of
    /// identical or near-identical control bytes instead of a deliberately
    /// noisy dither pattern. NO DITHERING IS APPLIED ANYWHERE IN THIS
    /// CODEC — a deliberate omission, not an oversight; see above.
    ///
    /// PALETTE IS 128 ENTRIES, RGB-ONLY (NOT 256/RGBA LIKE Indexed8) — a
    /// direct consequence of the byte layout below, not an independent
    /// choice: the mode bit consumes 1 of the 8 bits, leaving only 7 for a
    /// palette index, hence at most 128 distinct palette colors. RGB-only
    /// (no alpha channel in the palette or the delta math) because this
    /// format is used EXCLUSIVELY for Video-type MediaSourceNode frames
    /// (see ScrubProxyCache.EncodeFramePixels/ScrubFrameSource — Image/Text
    /// input nodes never go through a scrub proxy at all), and
    /// SkSourceDecoder's raw pipe always decodes video fully opaque — an
    /// alpha channel would cost real bits for a value that's always 255 in
    /// practice. Decode always writes A=255 accordingly (see Decode below).
    ///
    /// BYTE LAYOUT (one control byte per pixel), EXACTLY AS SPECIFIED:
    ///   bit 7 (MSB):  mode — 0 = PALETTE, 1 = DELTA.
    ///   PALETTE mode: bits 6-0 — a palette index, 0-127.
    ///   DELTA mode:   bits 6-5 — signed R modulation level (2 bits, 4
    ///                            steps: the eye is least sensitive to red
    ///                            among the three, so it gets the fewest
    ///                            bits along with blue).
    ///                 bits 4-2 — signed G modulation level (3 bits, 8
    ///                            steps: the eye is most sensitive to
    ///                            green, so it gets the most bits).
    ///                 bits 1-0 — signed B modulation level (2 bits, 4
    ///                            steps).
    /// Each level is stored as an unsigned field but represents a SIGNED
    /// delta centered on zero (level - halfRange), which is then scaled by
    /// a fixed per-channel step size (RStep/GStep/BStep below) and added to
    /// the corresponding channel of the PREVIOUS pixel (see ApplyDelta).
    ///
    /// STEP SIZES ARE TUNABLE STARTING POINTS, NOT VALIDATED AGAINST REAL
    /// CONTENT — flagged honestly: this environment has no compiler and no
    /// way to render/inspect a real frame, so RStep/GStep/BStep below were
    /// chosen by reasoning about plausible pixel-to-pixel deltas in
    /// low-resolution proxy video, not measured. If real-hardware testing
    /// shows visible banding within delta runs (steps too coarse) or a
    /// worse-than-expected palette-mode fallback rate (steps too fine to
    /// usefully cover typical deltas), these three constants are the first
    /// thing to retune — nothing else about the format needs to change to
    /// do that, since ApplyDelta is the only place step size is used.
    ///
    /// THE ENCODER MUST MIRROR THE DECODER'S OWN RECONSTRUCTED STATE, NOT
    /// THE ORIGINAL SOURCE PIXELS — the single most important correctness
    /// property of this codec, and NOT something the user's own pseudocode
    /// spelled out explicitly (its EncodePixel signature takes a `previous`
    /// Color without saying which one). A delta is only meaningful if the
    /// encoder computes it against the EXACT color the decoder will have
    /// already reconstructed for the pixel to the left — if the encoder
    /// instead used the ORIGINAL (pre-quantization) left pixel, the two
    /// sides would silently drift apart, compounding error every pixel
    /// along a row with no way for a decoder to ever detect or correct it.
    /// Encode therefore tracks its own "previous" as the actual chosen
    /// PALETTE or DELTA color for the pixel just written (`prevR/G/B` in
    /// the row loop below) — never the source's own left-neighbor value —
    /// and ApplyDelta (the one piece of arithmetic that turns a previous
    /// color + levels into a new color) is the SAME method Encode's own
    /// search and Decode both call, so the two can never drift apart by
    /// construction.
    ///
    /// ROW-START BOOTSTRAP (a boundary case the user's pseudocode didn't
    /// address): column 0 of every row has no left-neighbor to delta from
    /// at all — Encode/Decode both treat x=0 as PALETTE mode unconditionally,
    /// which needs no previous-pixel state to be well-defined. This also
    /// matches the user's own stated goal for the left-only dependency
    /// (\"you could calculate every row all at once\"): every row is
    /// independently decodable from nothing but its own bytes, so a future
    /// pass (CPU or GPU) could process every row of a frame in parallel —
    /// GPU-SHADER DECODE ITSELF IS STILL EXPLICITLY OUT OF SCOPE for this
    /// round, same as it already is for Indexed8 (see ScrubProxyCache's own
    /// remarks) — this format is decoded entirely on the CPU today, exactly
    /// like Indexed8, via ScrubProxyReader.
    ///
    /// PER-PIXEL SEARCH IS EXACT, NOT A PER-CHANNEL APPROXIMATION: for each
    /// pixel needing a delta candidate, FindBestDelta evaluates ALL
    /// 4 * 8 * 4 = 128 reachable (R, G, B) combinations against the SAME
    /// redmean-style distance metric FindNearestPaletteIndex uses for
    /// palette candidates (see DistanceSquared) — not an independent
    /// per-channel minimization, which would be cheaper but could pick a
    /// combination that isn't actually the closest under the metric that
    /// decides palette-vs-delta in the first place. 128 evaluations is
    /// trivial next to a real frame's own pixel count, and — like every
    /// cost in this codec — is paid exactly ONCE per proxy BUILD, never
    /// per scrub tick (see ScrubProxyCache.EncodeAsync's own remarks: a
    /// one-time linear pass, not a per-tick cost). A zero-delta EXACT match
    /// (this pixel's real color already equals its own left-neighbor's
    /// reconstructed color) is special-cased as an immediate, distance-0
    /// return before the full search runs at all — the extremely common
    /// case for flat regions and slow gradients, and the case that most
    /// directly produces the long identical-byte runs RLE compresses best.
    ///
    /// EXPERIMENTAL — NOT THE DEFAULT, NOT YET VALIDATED ON REAL CONTENT:
    /// EditSharpConfig.ScrubProxyPixelFormat still defaults to Indexed8 (a
    /// proven, already-tested-on-real-hardware format); IndexedDelta7 is
    /// available as an opt-in value for real-world evaluation, same as
    /// Indexed8 itself once was before its own hardware confirmation. See
    /// ScrubProxyPixelFormat.IndexedDelta7's own remarks for the on-disk
    /// shape this feeds and EditSharpConfig for the build-time knob.
    /// </summary>
    internal static class IndexedDelta7Codec
    {
        private const int PaletteSize = ScrubProxyFormat.Delta7PaletteEntryCount; // 128

        private const byte ModeBit = 0x80;
        private const byte PaletteIndexMask = 0x7F;

        private const int RLevelBits = 2, GLevelBits = 3, BLevelBits = 2;
        private const int RLevelCount = 1 << RLevelBits; // 4
        private const int GLevelCount = 1 << GLevelBits; // 8
        private const int BLevelCount = 1 << BLevelBits; // 4

        private const int ZeroRLevel = RLevelCount / 2; // 2 -> level 0
        private const int ZeroGLevel = GLevelCount / 2; // 4 -> level 0
        private const int ZeroBLevel = BLevelCount / 2; // 2 -> level 0

        // See class remarks, STEP SIZES ARE TUNABLE STARTING POINTS.
        private const int RStep = 6;
        private const int GStep = 4;
        private const int BStep = 6;

        /// <summary>
        /// Quantizes one RGBA8888 frame (`source`, exactly `width * height
        /// * 4` interleaved bytes, no row padding — same contract as
        /// ColorQuantizer.Quantize; alpha is read but never used, see class
        /// remarks) into a 128-entry RGB palette (`paletteOut`, exactly
        /// Delta7PaletteByteSize bytes) and one control byte per pixel
        /// (`pixelsOut`, exactly `width * height` bytes).
        /// </summary>
        public static void Encode(
            ReadOnlySpan<byte> source, int width, int height, Span<byte> paletteOut, Span<byte> pixelsOut)
        {
            int pixelCount = width * height;

            if (source.Length != pixelCount * 4)
                throw new ArgumentException(
                    $"source must be exactly {pixelCount * 4} bytes for a {width}x{height} RGBA8888 frame, got {source.Length}.",
                    nameof(source));
            if (paletteOut.Length != ScrubProxyFormat.Delta7PaletteByteSize)
                throw new ArgumentException(
                    $"paletteOut must be exactly {ScrubProxyFormat.Delta7PaletteByteSize} bytes.", nameof(paletteOut));
            if (pixelsOut.Length != pixelCount)
                throw new ArgumentException($"pixelsOut must be exactly {pixelCount} bytes.", nameof(pixelsOut));

            var histogram = new Dictionary<uint, int>();
            for (int i = 0; i < source.Length; i += 4)
            {
                uint key = PackRgb(source[i], source[i + 1], source[i + 2]);
                histogram[key] = histogram.TryGetValue(key, out int count) ? count + 1 : 1;
            }

            (uint Color, int Count)[] palette = BuildPalette(histogram);
            WritePalette(palette, paletteOut);

            // Cached per DISTINCT SOURCE COLOR (not per pixel, not per
            // (color, previous) pair — see class remarks, PER-PIXEL SEARCH
            // IS EXACT: a color's nearest palette candidate never depends
            // on the previous pixel, only the delta candidate does, and
            // that search is cheap enough (128 evals) not to need caching
            // of its own).
            var nearestPaletteCache = new Dictionary<uint, (byte Index, byte R, byte G, byte B)>();

            for (int y = 0; y < height; y++)
            {
                byte prevR = 0, prevG = 0, prevB = 0;

                for (int x = 0; x < width; x++)
                {
                    int pixelOffset = (y * width + x) * 4;
                    byte r = source[pixelOffset];
                    byte g = source[pixelOffset + 1];
                    byte b = source[pixelOffset + 2];

                    uint sourceKey = PackRgb(r, g, b);
                    if (!nearestPaletteCache.TryGetValue(sourceKey, out var nearest))
                    {
                        byte idx = FindNearestPaletteIndex(palette, r, g, b);
                        UnpackRgb(palette[idx].Color, out byte pr, out byte pg, out byte pb);
                        nearest = (idx, pr, pg, pb);
                        nearestPaletteCache[sourceKey] = nearest;
                    }

                    byte code;
                    byte chosenR, chosenG, chosenB;

                    if (x == 0)
                    {
                        // See class remarks, ROW-START BOOTSTRAP — no
                        // left-neighbor exists yet, so this column is
                        // unconditionally PALETTE mode (mode bit already 0
                        // via nearest.Index, which is always <= 127).
                        code = nearest.Index;
                        chosenR = nearest.R; chosenG = nearest.G; chosenB = nearest.B;
                    }
                    else
                    {
                        long paletteDistance = DistanceSquared(r, g, b, nearest.R, nearest.G, nearest.B);

                        (byte deltaCode, byte dr, byte dg, byte db, long deltaDistance) =
                            FindBestDelta(prevR, prevG, prevB, r, g, b);

                        // Whichever candidate is actually closer to the
                        // real source pixel wins — exactly the user's own
                        // stated rule ("whichever color is closer to
                        // original"). Ties favor palette mode arbitrarily
                        // (<=) — no behavioral significance either way.
                        if (paletteDistance <= deltaDistance)
                        {
                            code = nearest.Index;
                            chosenR = nearest.R; chosenG = nearest.G; chosenB = nearest.B;
                        }
                        else
                        {
                            code = (byte)(ModeBit | deltaCode);
                            chosenR = dr; chosenG = dg; chosenB = db;
                        }
                    }

                    pixelsOut[y * width + x] = code;

                    // See class remarks, THE ENCODER MUST MIRROR THE
                    // DECODER'S OWN RECONSTRUCTED STATE — `prev` becomes
                    // whichever color was actually CHOSEN (and will
                    // therefore actually be decoded), never the original
                    // source pixel.
                    prevR = chosenR; prevG = chosenG; prevB = chosenB;
                }
            }
        }

        /// <summary>
        /// Expands one IndexedDelta7 frame back to full RGBA8888 — the
        /// read-side counterpart to Encode, used by
        /// ScrubProxyReader.GetFrameAt. Alpha is always written as 255
        /// (opaque) — see class remarks on why this format carries no
        /// alpha information at all.
        /// </summary>
        public static void Decode(
            ReadOnlySpan<byte> palette, ReadOnlySpan<byte> pixels, int width, int height, Span<byte> destination)
        {
            int pixelCount = width * height;

            if (palette.Length != ScrubProxyFormat.Delta7PaletteByteSize)
                throw new ArgumentException(
                    $"palette must be exactly {ScrubProxyFormat.Delta7PaletteByteSize} bytes.", nameof(palette));
            if (pixels.Length != pixelCount)
                throw new ArgumentException($"pixels must be exactly {pixelCount} bytes.", nameof(pixels));
            if (destination.Length != pixelCount * 4)
                throw new ArgumentException(
                    $"destination must be exactly {pixelCount * 4} bytes for {pixelCount} pixels.", nameof(destination));

            for (int y = 0; y < height; y++)
            {
                byte prevR = 0, prevG = 0, prevB = 0;

                for (int x = 0; x < width; x++)
                {
                    byte code = pixels[y * width + x];
                    byte r, g, b;

                    if ((code & ModeBit) == 0)
                    {
                        int idx = code & PaletteIndexMask;
                        int paletteOffset = idx * 3;
                        r = palette[paletteOffset];
                        g = palette[paletteOffset + 1];
                        b = palette[paletteOffset + 2];
                    }
                    else
                    {
                        int rLevel = ((code >> (GLevelBits + BLevelBits)) & (RLevelCount - 1)) - ZeroRLevel;
                        int gLevel = ((code >> BLevelBits) & (GLevelCount - 1)) - ZeroGLevel;
                        int bLevel = (code & (BLevelCount - 1)) - ZeroBLevel;

                        ApplyDelta(prevR, prevG, prevB, rLevel, gLevel, bLevel, out r, out g, out b);
                    }

                    int destOffset = (y * width + x) * 4;
                    destination[destOffset] = r;
                    destination[destOffset + 1] = g;
                    destination[destOffset + 2] = b;
                    destination[destOffset + 3] = 255;

                    prevR = r; prevG = g; prevB = b;
                }
            }
        }

        /// <summary>
        /// The ONE place previous-color + signed levels turns into an
        /// actual channel value — called by BOTH Encode's own delta search
        /// (via FindBestDelta) and Decode, so the two can never compute the
        /// delta math differently. See class remarks, THE ENCODER MUST
        /// MIRROR THE DECODER'S OWN RECONSTRUCTED STATE.
        /// </summary>
        private static void ApplyDelta(
            byte prevR, byte prevG, byte prevB, int rLevel, int gLevel, int bLevel,
            out byte r, out byte g, out byte b)
        {
            r = (byte)Math.Clamp(prevR + rLevel * RStep, 0, 255);
            g = (byte)Math.Clamp(prevG + gLevel * GStep, 0, 255);
            b = (byte)Math.Clamp(prevB + bLevel * BStep, 0, 255);
        }

        /// <summary>
        /// The best of all 4*8*4=128 reachable delta candidates from
        /// (prevR, prevG, prevB) toward (targetR, targetG, targetB), under
        /// the same DistanceSquared metric palette candidates are scored
        /// with — see class remarks, PER-PIXEL SEARCH IS EXACT. The
        /// zero-delta exact-match fast path below is an optimization only
        /// — the general loop would find the identical answer (distance
        /// 0) on its own, just after needlessly evaluating 127 other
        /// combinations first.
        /// </summary>
        private static (byte Code, byte R, byte G, byte B, long Distance) FindBestDelta(
            byte prevR, byte prevG, byte prevB, byte targetR, byte targetG, byte targetB)
        {
            if (prevR == targetR && prevG == targetG && prevB == targetB)
            {
                byte zeroCode = (byte)((ZeroRLevel << (GLevelBits + BLevelBits)) | (ZeroGLevel << BLevelBits) | ZeroBLevel);
                return (zeroCode, prevR, prevG, prevB, 0);
            }

            byte bestCode = 0;
            byte bestR = prevR, bestG = prevG, bestB = prevB;
            long bestDistance = long.MaxValue;

            for (int rl = 0; rl < RLevelCount; rl++)
            {
                for (int gl = 0; gl < GLevelCount; gl++)
                {
                    for (int bl = 0; bl < BLevelCount; bl++)
                    {
                        ApplyDelta(
                            prevR, prevG, prevB, rl - ZeroRLevel, gl - ZeroGLevel, bl - ZeroBLevel,
                            out byte candR, out byte candG, out byte candB);

                        long distance = DistanceSquared(targetR, targetG, targetB, candR, candG, candB);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            bestCode = (byte)((rl << (GLevelBits + BLevelBits)) | (gl << BLevelBits) | bl);
                            bestR = candR; bestG = candG; bestB = candB;
                        }
                    }
                }
            }

            return (bestCode, bestR, bestG, bestB, bestDistance);
        }

        /// <summary>
        /// Median-cut over `histogram`'s distinct (color, count) entries in
        /// RGB (3D) space, splitting buckets until PaletteSize (128)
        /// buckets exist or every bucket is down to one distinct color —
        /// same algorithm shape as ColorQuantizer.BuildPalette, kept as an
        /// independent copy rather than a shared/refactored helper
        /// deliberately: this format is new and unvalidated (see class
        /// remarks, EXPERIMENTAL), and refactoring ColorQuantizer's own
        /// already-hardware-confirmed Indexed8 path to share code with it
        /// would risk that proven path for no benefit to either format —
        /// the two are RGB vs RGBA, 128 vs 256 buckets, and diverge in the
        /// caller's own per-pixel loop shape (this format's row-sequential
        /// delta search has no Indexed8 equivalent at all), so very little
        /// would actually be shared besides the median-cut skeleton itself.
        /// </summary>
        private static (uint Color, int Count)[] BuildPalette(Dictionary<uint, int> histogram)
        {
            var initial = new List<(uint Color, int Count)>(histogram.Count);
            foreach (KeyValuePair<uint, int> entry in histogram)
                initial.Add((entry.Key, entry.Value));

            var buckets = new List<List<(uint Color, int Count)>> { initial };

            while (buckets.Count < PaletteSize)
            {
                int splitIndex = -1;
                int splitWeight = -1;
                for (int i = 0; i < buckets.Count; i++)
                {
                    if (buckets[i].Count <= 1) continue;
                    int weight = SumCount(buckets[i]);
                    if (weight > splitWeight)
                    {
                        splitWeight = weight;
                        splitIndex = i;
                    }
                }

                if (splitIndex < 0) break;

                (List<(uint Color, int Count)> a, List<(uint Color, int Count)> b) = SplitBucket(buckets[splitIndex]);
                buckets.RemoveAt(splitIndex);
                buckets.Add(a);
                buckets.Add(b);
            }

            var palette = new (uint Color, int Count)[PaletteSize];
            int written = 0;
            foreach (List<(uint Color, int Count)> bucket in buckets)
            {
                palette[written++] = AverageBucket(bucket);
                if (written == PaletteSize) break;
            }

            (uint Color, int Count) last = written > 0 ? palette[written - 1] : (PackRgb(0, 0, 0), 0);
            for (int i = written; i < PaletteSize; i++)
                palette[i] = last;

            return palette;
        }

        private static int SumCount(List<(uint Color, int Count)> bucket)
        {
            int total = 0;
            foreach ((uint _, int count) in bucket) total += count;
            return total;
        }

        private static (List<(uint Color, int Count)> Low, List<(uint Color, int Count)> High) SplitBucket(
            List<(uint Color, int Count)> bucket)
        {
            byte minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
            foreach ((uint color, int _) in bucket)
            {
                UnpackRgb(color, out byte r, out byte g, out byte b);
                if (r < minR) minR = r; if (r > maxR) maxR = r;
                if (g < minG) minG = g; if (g > maxG) maxG = g;
                if (b < minB) minB = b; if (b > maxB) maxB = b;
            }

            int rangeR = maxR - minR, rangeG = maxG - minG, rangeB = maxB - minB;
            int widest = Math.Max(rangeR, Math.Max(rangeG, rangeB));

            Func<uint, int> channelSelector;
            if (widest == rangeR) channelSelector = c => { UnpackRgb(c, out byte r, out _, out _); return r; };
            else if (widest == rangeG) channelSelector = c => { UnpackRgb(c, out _, out byte g, out _); return g; };
            else channelSelector = c => { UnpackRgb(c, out _, out _, out byte b); return b; };

            bucket.Sort((x, y) => channelSelector(x.Color).CompareTo(channelSelector(y.Color)));

            int totalWeight = SumCount(bucket);
            int half = totalWeight / 2;
            int cumulative = 0;
            int splitAt = 1;

            for (int i = 0; i < bucket.Count; i++)
            {
                cumulative += bucket[i].Count;
                if (cumulative >= half)
                {
                    splitAt = Math.Clamp(i + 1, 1, bucket.Count - 1);
                    break;
                }
            }

            var low = bucket.GetRange(0, splitAt);
            var high = bucket.GetRange(splitAt, bucket.Count - splitAt);
            return (low, high);
        }

        private static (uint Color, int Count) AverageBucket(List<(uint Color, int Count)> bucket)
        {
            long sumR = 0, sumG = 0, sumB = 0;
            long totalWeight = 0;

            foreach ((uint color, int count) in bucket)
            {
                UnpackRgb(color, out byte r, out byte g, out byte b);
                sumR += (long)r * count;
                sumG += (long)g * count;
                sumB += (long)b * count;
                totalWeight += count;
            }

            if (totalWeight == 0) return (PackRgb(0, 0, 0), 0);

            byte avgR = (byte)(sumR / totalWeight);
            byte avgG = (byte)(sumG / totalWeight);
            byte avgB = (byte)(sumB / totalWeight);

            return (PackRgb(avgR, avgG, avgB), (int)totalWeight);
        }

        private static byte FindNearestPaletteIndex((uint Color, int Count)[] palette, int r, int g, int b)
        {
            byte best = 0;
            long bestDistance = long.MaxValue;

            for (int i = 0; i < palette.Length; i++)
            {
                UnpackRgb(palette[i].Color, out byte pr, out byte pg, out byte pb);
                long distance = DistanceSquared(r, g, b, pr, pg, pb);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = (byte)i;
                }
            }

            return best;
        }

        /// <summary>
        /// Redmean-style perceptually-weighted squared distance on RGB —
        /// identical formula/weights to ColorQuantizer's own
        /// ComputeDistanceSquared, minus the alpha term (this format never
        /// carries alpha — see class remarks). Kept as its own copy for
        /// the same reason BuildPalette is: see BuildPalette's own remarks.
        /// </summary>
        private static long DistanceSquared(int r1, int g1, int b1, int r2, int g2, int b2)
        {
            long rMean = (r1 + r2) / 2;
            long dr = r1 - r2;
            long dg = g1 - g2;
            long db = b1 - b2;

            long weightR = 512 + rMean;
            long weightB = 767 - rMean;
            long weightG = 1024;

            return (weightR * dr * dr) + (weightG * dg * dg) + (weightB * db * db);
        }

        private static void WritePalette((uint Color, int Count)[] palette, Span<byte> paletteOut)
        {
            for (int i = 0; i < palette.Length; i++)
            {
                UnpackRgb(palette[i].Color, out byte r, out byte g, out byte b);
                int offset = i * 3;
                paletteOut[offset] = r;
                paletteOut[offset + 1] = g;
                paletteOut[offset + 2] = b;
            }
        }

        private static uint PackRgb(byte r, byte g, byte b) => ((uint)r << 16) | ((uint)g << 8) | b;

        private static void UnpackRgb(uint color, out byte r, out byte g, out byte b)
        {
            r = (byte)(color >> 16);
            g = (byte)(color >> 8);
            b = (byte)color;
        }
    }
}