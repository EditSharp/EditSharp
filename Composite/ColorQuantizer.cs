using System;
using System.Collections.Generic;

namespace EditSharp.Composite
{
    /// <summary>
    /// Builds a (at most) 256-color RGBA8888 palette for one frame and maps
    /// every pixel to its nearest palette index — the quantization
    /// machinery behind ScrubProxyPixelFormat.Indexed8 (see that enum's own
    /// remarks for the on-disk shape this feeds, and EditSharpConfig.
    /// ScrubProxyPixelFormat for the build-time knob that turns it on).
    /// DECIDED IN CONVERSATION, direct response to a user proposal, with
    /// three explicit design choices confirmed before implementation:
    ///   1. PROPER COLOR QUANTIZATION (median-cut, weighted-average
    ///      buckets), not nearest-exact-match against the raw pixel set —
    ///      "I was referring to proper color quantization rather than
    ///      trying to find exact matches in the raw pixels."
    ///   2. FULL RGBA (4D) quantization space, not RGB-only — a proxy frame
    ///      participates in real alpha compositing (see ScrubFrameSource/
    ///      SkFrameCompositor), so flattening alpha to whatever a RGB-only
    ///      palette happened to carry would be a real, visible regression,
    ///      not just a color-accuracy one.
    ///   3. ORDERED (BAYER) DITHERING for banding reduction — "yes, let's
    ///      implement the banding reduction" — chosen over error-diffusion
    ///      dithering (Floyd-Steinberg and similar) specifically because it
    ///      is STATELESS AND PER-PIXEL: no carried error term between
    ///      pixels, so nothing here needs to process a frame in a fixed
    ///      scan order or maintain any cross-pixel state, which keeps this
    ///      a trivially parallelizable, allocation-light, one-time-per-
    ///      frame pass (this runs once per frame at PROXY BUILD time, never
    ///      per scrub tick — see ScrubProxyCache.EncodeAsync/
    ///      EncodeFramePixels — so it does not need to be as cheap as the
    ///      per-tick GetFrameAt path, but there is no reason to make it
    ///      needlessly expensive either).
    ///
    /// MEDIAN CUT OVER K-MEANS: median cut is fully deterministic (same
    /// input always produces the same palette — no random seed, no
    /// convergence criterion, no risk of a frame's palette flickering
    /// between runs of the same build), and non-iterative (a single,
    /// bounded recursive partition, rather than repeated reassignment
    /// passes until convergence) — simpler to reason about and to keep
    /// fast at build time, with palette quality that's more than adequate
    /// for a scrub PREVIEW (this is never used for a final render — see
    /// ScrubProxyFormat's own class remarks on the "accurate to the proxy,
    /// not the source" trade-off this whole mechanism already makes).
    ///
    /// NEAREST-NEIGHBOR DISTANCE IS A REDMEAN-STYLE PERCEPTUALLY-WEIGHTED
    /// METRIC ON RGB, PLUS A SEPARATE ALPHA TERM — chosen over naive
    /// Euclidean RGB distance because plain Euclidean distance in RGB space
    /// does not match how humans actually perceive color difference (it
    /// over-penalizes some hues and under-penalizes others depending on
    /// where in the space the comparison falls), which shows up as visible
    /// banding/artifacts right where naive distance and perceptual
    /// difference disagree most. Redmean weights the R/B terms by where the
    /// pair sits along the red axis (see ComputeDistanceSquared below for
    /// the exact formula) — a well-known, cheap approximation of a true
    /// perceptual metric (e.g. CIEDE2000) that needs no color-space
    /// conversion at all, unlike Lab-based metrics.
    /// </summary>
    internal static class ColorQuantizer
    {
        private const int PaletteSize = ScrubProxyFormat.IndexedPaletteEntryCount;

        /// <summary>
        /// Classic 4x4 Bayer ordered-dither matrix, entries in [0, 15] —
        /// see class remarks for why ordered/stateless dithering was chosen
        /// over error diffusion. Values below are the standard bit-reversal
        /// construction of the 4x4 Bayer matrix.
        /// </summary>
        private static readonly int[,] BayerMatrix4x4 =
        {
            { 0, 8, 2, 10 },
            { 12, 4, 14, 6 },
            { 3, 11, 1, 9 },
            { 15, 7, 13, 5 },
        };

        /// <summary>
        /// How far a dither nudge can push a channel before nearest-
        /// neighbor lookup, at the STRONGEST Bayer level — roughly "half a
        /// palette quantization step" for a uniformly-spaced 256-entry
        /// palette (256 colors over 256 levels per channel in the fully-
        /// populated case averages out to close to 1 level per entry, so a
        /// nudge much larger than this would visibly shift color identity
        /// rather than just break up banding). Kept modest and fixed rather
        /// than adaptive per-frame — this is a preview proxy, not a final
        /// render, so a single reasonable constant that works acceptably
        /// across typical content is preferable to extra per-frame
        /// analysis for a marginal gain.
        /// </summary>
        private const int DitherStrength = 12;

        /// <summary>
        /// Quantizes one RGBA8888 frame (`source`, exactly `width * height
        /// * 4` interleaved bytes, no row padding — matches
        /// SKPixmap.GetPixelSpan's own shape) into a 256-entry RGBA8888
        /// palette (`paletteOut`, exactly 1024 bytes) and one palette index
        /// per pixel (`indexOut`, exactly `width * height` bytes).
        ///
        /// Unused palette slots (when a frame has fewer than 256 distinct
        /// median-cut buckets — i.e. fewer than 256 distinct colors overall)
        /// are filled with a copy of the last real bucket's color rather
        /// than left as zeroed/transparent black — harmless either way
        /// since no index ever points at a genuinely unused slot, but
        /// avoids a palette with a suspicious block of pure-transparent-
        /// black entries if anything ever inspects the palette directly.
        /// </summary>
        public static void Quantize(ReadOnlySpan<byte> source, int width, int height, Span<byte> paletteOut, Span<byte> indexOut)
        {
            int pixelCount = width * height;

            if (source.Length != pixelCount * 4)
                throw new ArgumentException(
                    $"source must be exactly {pixelCount * 4} bytes for a {width}x{height} RGBA8888 frame, got {source.Length}.",
                    nameof(source));
            if (paletteOut.Length != ScrubProxyFormat.IndexedPaletteByteSize)
                throw new ArgumentException(
                    $"paletteOut must be exactly {ScrubProxyFormat.IndexedPaletteByteSize} bytes.", nameof(paletteOut));
            if (indexOut.Length != pixelCount)
                throw new ArgumentException($"indexOut must be exactly {pixelCount} bytes.", nameof(indexOut));

            // Step 1: histogram every DISTINCT color in the frame, packed
            // as a single uint key (R<<24 | G<<16 | B<<8 | A) so buckets
            // below can sort/compare/split on it cheaply. This also means
            // the (usually much smaller) set of distinct colors, not the
            // full pixel count, drives every subsequent step's cost.
            var histogram = new Dictionary<uint, int>();
            for (int i = 0; i < source.Length; i += 4)
            {
                uint key = PackColor(source[i], source[i + 1], source[i + 2], source[i + 3]);
                histogram[key] = histogram.TryGetValue(key, out int count) ? count + 1 : 1;
            }

            // Step 2: build the palette via median cut over that histogram.
            (uint Color, int Count)[] palette = BuildPalette(histogram);
            WritePalette(palette, paletteOut);

            // Step 3: map every pixel to its nearest palette index, with an
            // ordered-dither nudge applied to the SOURCE color before the
            // nearest-neighbor search (not to the chosen palette color
            // afterwards) — see class remarks and ApplyDither below.
            // Cached per (distinct color, dither level) rather than per
            // pixel — a 4x4 Bayer matrix has only 16 distinct levels, so
            // this bounds the real number of nearest-neighbor searches to
            // (distinct colors * 16) rather than (width * height), which
            // matters since the search itself is an O(paletteSize) scan.
            var nearestCache = new Dictionary<(uint Color, int DitherLevel), byte>();

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int pixelOffset = (y * width + x) * 4;
                    byte r = source[pixelOffset];
                    byte g = source[pixelOffset + 1];
                    byte b = source[pixelOffset + 2];
                    byte a = source[pixelOffset + 3];

                    int ditherLevel = BayerMatrix4x4[y & 3, x & 3];
                    uint originalKey = PackColor(r, g, b, a);

                    if (!nearestCache.TryGetValue((originalKey, ditherLevel), out byte nearestIndex))
                    {
                        (int dr, int dg, int db, int da) = ApplyDither(r, g, b, a, ditherLevel);
                        nearestIndex = FindNearestPaletteIndex(palette, dr, dg, db, da);
                        nearestCache[(originalKey, ditherLevel)] = nearestIndex;
                    }

                    indexOut[y * width + x] = nearestIndex;
                }
            }
        }

        /// <summary>
        /// Nudges each channel by a small signed offset derived from this
        /// pixel's position in the 4x4 Bayer matrix (ditherLevel in
        /// [0, 15]), centered so the AVERAGE nudge across a whole 4x4 tile
        /// is zero — this is what keeps ordered dithering from biasing the
        /// image's overall brightness/color while still breaking up flat
        /// banding into a stable, repeating pattern the eye perceptually
        /// averages back out. Clamped to [0, 255] per channel since the
        /// result only ever feeds a distance calculation, never gets
        /// stored directly.
        /// </summary>
        private static (int R, int G, int B, int A) ApplyDither(byte r, byte g, byte b, byte a, int ditherLevel)
        {
            // ditherLevel in [0, 15] -> offset in [-DitherStrength/2, +DitherStrength/2), centered at 7.5.
            double normalized = (ditherLevel - 7.5) / 15.0; // in [-0.5, +0.5)
            int offset = (int)Math.Round(normalized * DitherStrength);

            return (
                Math.Clamp(r + offset, 0, 255),
                Math.Clamp(g + offset, 0, 255),
                Math.Clamp(b + offset, 0, 255),
                Math.Clamp(a + offset, 0, 255));
        }

        /// <summary>
        /// Median-cut over `histogram`'s distinct (color, count) entries,
        /// splitting buckets until PaletteSize buckets exist (or every
        /// bucket is down to one distinct color, whichever comes first),
        /// then averaging each bucket (weighted by pixel count) into one
        /// final palette color. See class remarks for why median cut over
        /// k-means.
        /// </summary>
        private static (uint Color, int Count)[] BuildPalette(Dictionary<uint, int> histogram)
        {
            var initial = new List<(uint Color, int Count)>(histogram.Count);
            foreach (KeyValuePair<uint, int> entry in histogram)
                initial.Add((entry.Key, entry.Value));

            var buckets = new List<List<(uint Color, int Count)>> { initial };

            // Repeatedly split the bucket with the largest total pixel
            // weight (a population-weighted split target biases splitting
            // toward the colors that actually cover the most of the frame,
            // which is what a scrub-preview palette should prioritize)
            // until we have PaletteSize buckets or nothing left splittable.
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

                if (splitIndex < 0) break; // every remaining bucket is a single distinct color — nothing left to split

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

            // Pad any unused trailing slots (fewer distinct buckets than
            // PaletteSize) with a copy of the last real entry — see the
            // public Quantize doc comment for why.
            (uint Color, int Count) last = written > 0 ? palette[written - 1] : (PackColor(0, 0, 0, 0), 0);
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

        /// <summary>
        /// Splits `bucket` in full RGBA (4D) space: finds whichever of
        /// R/G/B/A has the widest raw value range across the bucket's
        /// distinct colors (unweighted min/max — the standard median-cut
        /// choice of split axis), sorts the bucket by that channel, and
        /// splits at the pixel-count-weighted median (the point where
        /// cumulative weight first reaches half the bucket's total weight)
        /// rather than the midpoint index — so a bucket dominated by one
        /// very common color doesn't get split lopsidedly away from where
        /// most of its actual pixels sit.
        /// </summary>
        private static (List<(uint Color, int Count)> Low, List<(uint Color, int Count)> High) SplitBucket(
            List<(uint Color, int Count)> bucket)
        {
            byte minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0, minA = 255, maxA = 0;
            foreach ((uint color, int _) in bucket)
            {
                UnpackColor(color, out byte r, out byte g, out byte b, out byte a);
                if (r < minR) minR = r; if (r > maxR) maxR = r;
                if (g < minG) minG = g; if (g > maxG) maxG = g;
                if (b < minB) minB = b; if (b > maxB) maxB = b;
                if (a < minA) minA = a; if (a > maxA) maxA = a;
            }

            int rangeR = maxR - minR, rangeG = maxG - minG, rangeB = maxB - minB, rangeA = maxA - minA;
            int widest = Math.Max(Math.Max(rangeR, rangeG), Math.Max(rangeB, rangeA));

            // Ties break in a fixed R > G > B > A order — arbitrary but
            // deterministic, matching median cut's own no-randomness goal.
            Func<uint, int> channelSelector;
            if (widest == rangeR) channelSelector = c => { UnpackColor(c, out byte r, out _, out _, out _); return r; };
            else if (widest == rangeG) channelSelector = c => { UnpackColor(c, out _, out byte g, out _, out _); return g; };
            else if (widest == rangeB) channelSelector = c => { UnpackColor(c, out _, out _, out byte b, out _); return b; };
            else channelSelector = c => { UnpackColor(c, out _, out _, out _, out byte a); return a; };

            bucket.Sort((x, y) => channelSelector(x.Color).CompareTo(channelSelector(y.Color)));

            int totalWeight = SumCount(bucket);
            int half = totalWeight / 2;
            int cumulative = 0;
            int splitAt = 1; // always leave at least one entry in the low bucket

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

        /// <summary>Pixel-count-weighted average RGBA of every distinct color in `bucket`.</summary>
        private static (uint Color, int Count) AverageBucket(List<(uint Color, int Count)> bucket)
        {
            long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
            long totalWeight = 0;

            foreach ((uint color, int count) in bucket)
            {
                UnpackColor(color, out byte r, out byte g, out byte b, out byte a);
                sumR += (long)r * count;
                sumG += (long)g * count;
                sumB += (long)b * count;
                sumA += (long)a * count;
                totalWeight += count;
            }

            if (totalWeight == 0) return (PackColor(0, 0, 0, 0), 0);

            byte avgR = (byte)(sumR / totalWeight);
            byte avgG = (byte)(sumG / totalWeight);
            byte avgB = (byte)(sumB / totalWeight);
            byte avgA = (byte)(sumA / totalWeight);

            return (PackColor(avgR, avgG, avgB, avgA), (int)totalWeight);
        }

        /// <summary>
        /// Brute-force nearest-neighbor scan of all PaletteSize entries —
        /// see class remarks on why this is affordable here (a one-time
        /// per-frame build cost, cached per distinct-color-and-dither-level
        /// pair by the caller, not a per-tick cost at all).
        /// </summary>
        private static byte FindNearestPaletteIndex(
            (uint Color, int Count)[] palette, int r, int g, int b, int a)
        {
            byte best = 0;
            long bestDistance = long.MaxValue;

            for (int i = 0; i < palette.Length; i++)
            {
                UnpackColor(palette[i].Color, out byte pr, out byte pg, out byte pb, out byte pa);
                long distance = ComputeDistanceSquared(r, g, b, a, pr, pg, pb, pa);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = (byte)i;
                }
            }

            return best;
        }

        /// <summary>
        /// Redmean-style perceptually-weighted squared distance on RGB
        /// (see https://www.compuphase.com/cmetric.htm for the standard
        /// formula this follows), plus a separate weighted squared-alpha
        /// term — alpha has no equivalent perceptual literature to draw a
        /// weighting from, so it's weighted equally to the green term
        /// (redmean's own highest fixed RGB weight) on the reasoning that
        /// an alpha error is exactly as visually significant as a green
        /// error for content that will be composited over other layers
        /// (see ScrubProxyPixelFormat.Indexed8's own remarks on why alpha
        /// is quantized at all here).
        /// </summary>
        private static long ComputeDistanceSquared(int r1, int g1, int b1, int a1, int r2, int g2, int b2, int a2)
        {
            long rMean = (r1 + r2) / 2;
            long dr = r1 - r2;
            long dg = g1 - g2;
            long db = b1 - b2;
            long da = a1 - a2;

            // Scaled by 512 throughout (matching the standard redmean
            // formula's own weighting constants scaled up to stay in
            // integer arithmetic) — only relative ordering matters for a
            // nearest-neighbor comparison, so the overall scale is
            // irrelevant as long as it's applied consistently.
            long weightR = 512 + rMean;
            long weightB = 767 - rMean; // 512 + (255 - rMean)
            long weightG = 1024; // 4 * 256, redmean's fixed green weight
            long weightA = 1024; // matched to green — see doc comment above

            return (weightR * dr * dr) + (weightG * dg * dg) + (weightB * db * db) + (weightA * da * da);
        }

        /// <summary>Writes `palette`'s 256 entries as raw interleaved RGBA8888 bytes into `paletteOut`.</summary>
        private static void WritePalette((uint Color, int Count)[] palette, Span<byte> paletteOut)
        {
            for (int i = 0; i < palette.Length; i++)
            {
                UnpackColor(palette[i].Color, out byte r, out byte g, out byte b, out byte a);
                int offset = i * 4;
                paletteOut[offset] = r;
                paletteOut[offset + 1] = g;
                paletteOut[offset + 2] = b;
                paletteOut[offset + 3] = a;
            }
        }

        private static uint PackColor(byte r, byte g, byte b, byte a) =>
            ((uint)r << 24) | ((uint)g << 16) | ((uint)b << 8) | a;

        private static void UnpackColor(uint color, out byte r, out byte g, out byte b, out byte a)
        {
            r = (byte)(color >> 24);
            g = (byte)(color >> 16);
            b = (byte)(color >> 8);
            a = (byte)color;
        }

        /// <summary>
        /// Expands one Indexed8 frame back to full RGBA8888 — the read-side
        /// counterpart to Quantize, used by ScrubProxyReader.GetFrameAt.
        /// Trivial and allocation-free: one palette lookup (4-byte copy)
        /// per pixel, no distance computation or dithering involved at all
        /// (dithering only ever affects which palette index gets CHOSEN at
        /// build time — decoding is always an exact, lossless replay of
        /// whichever indices were stored).
        /// </summary>
        public static void Expand(ReadOnlySpan<byte> palette, ReadOnlySpan<byte> indices, Span<byte> destination)
        {
            if (palette.Length != ScrubProxyFormat.IndexedPaletteByteSize)
                throw new ArgumentException(
                    $"palette must be exactly {ScrubProxyFormat.IndexedPaletteByteSize} bytes.", nameof(palette));
            if (destination.Length != indices.Length * 4)
                throw new ArgumentException(
                    $"destination must be exactly {indices.Length * 4} bytes for {indices.Length} indices.", nameof(destination));

            for (int i = 0; i < indices.Length; i++)
            {
                int paletteOffset = indices[i] * 4;
                int destOffset = i * 4;
                destination[destOffset] = palette[paletteOffset];
                destination[destOffset + 1] = palette[paletteOffset + 1];
                destination[destOffset + 2] = palette[paletteOffset + 2];
                destination[destOffset + 3] = palette[paletteOffset + 3];
            }
        }
    }
}