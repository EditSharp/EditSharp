using System;
using System.Collections.Generic;

namespace EditSharp.Caching.ScrubProxy
{
    /// <summary>
    /// Encoder/decoder for ScrubProxyPixelFormat.IndexedDelta7 — DIRECT
    /// IMPLEMENTATION OF A USER-PROPOSED PSEUDOCODE DESIGN: every pixel is
    /// stored as ONE control byte that is EITHER "the nearest color in this
    /// file's own small palette" OR "a small modulation of the pixel
    /// immediately to my left," whichever one lands closer to the real
    /// source color — chosen per-pixel, not per-frame.
    ///
    /// NO DITHERING IS APPLIED ANYWHERE IN THIS CODEC — a deliberate
    /// omission, not an oversight: a pixel that's close to, but not
    /// exactly, a palette color doesn't need a faked-in neighboring
    /// variation to read as smooth — it can instead land on an exact small
    /// delta from its own already-chosen left neighbor, which (a) is
    /// visually more accurate than a dithered approximation and (b) is
    /// dramatically more compression-friendly, since a flat OR smoothly-
    /// gradient region now tends to produce long runs of identical or
    /// near-identical control bytes instead of a deliberately noisy dither
    /// pattern.
    ///
    /// PALETTE IS 128 ENTRIES, RGB-ONLY — a direct consequence of the byte
    /// layout below, not an independent choice: the mode bit consumes 1 of
    /// the 8 bits, leaving only 7 for a palette index, hence at most 128
    /// distinct palette colors. RGB-only (no alpha channel in the palette
    /// or the delta math) because this format is used EXCLUSIVELY for
    /// Video-type VideoSourceNode frames (see
    /// ScrubProxyCache.EncodeFramePixels/ScrubFrameSource — Image/Text
    /// input nodes never go through a scrub proxy at all), and
    /// SourceDecoder's raw pipe always decodes video fully opaque — an
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
    /// do that, since ApplyDelta is the only place step size is used. Noted
    /// but NOT acted on this round: a luma-weighted re-split of the delta
    /// bit budget, and a larger/alternate palette size, are both real
    /// levers still on the table for the 720p30 size target — see
    /// EditSharpConfig.ScrubProxyPixelFormat's own remarks.
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
    /// The row loop therefore tracks its own "previous" as the actual
    /// chosen PALETTE or DELTA color for the pixel just written
    /// (`prevR/G/B`) — never the source's own left-neighbor value — and
    /// ApplyDelta (the one piece of arithmetic that turns a previous color
    /// + levels into a new color) is the SAME method the encode-side
    /// search and Decode both call, so the two can never drift apart by
    /// construction.
    ///
    /// ROW-START BOOTSTRAP (a boundary case the user's pseudocode didn't
    /// address): column 0 of every row has no left-neighbor to delta from
    /// at all — encode/decode both treat x=0 as PALETTE mode
    /// unconditionally, which needs no previous-pixel state to be
    /// well-defined. This also matches the user's own stated goal for the
    /// left-only dependency ("you could calculate every row all at once"):
    /// every row is independently decodable from nothing but its own
    /// bytes, so a parallel pass could in principle process every row of a
    /// frame independently. NO GPU PATH ACTUALLY EXISTS IN THIS CODEBASE
    /// FOR EITHER DIRECTION, THOUGH, BOTH TRIED AND BOTH REMOVED: an
    /// earlier GPU decode attempt was removed as unneeded complexity,
    /// since a proxy read is already a cheap, single positioned file read
    /// regardless of pixel format (see ScrubFrameSource's own remarks);
    /// and a later GPU encode attempt (ScrubProxyGpuEncoder, since
    /// deleted) was measured to be dramatically slower than this plain
    /// CPU path — its own per-column sequential draw-call structure ended
    /// up dominated by GPU submission overhead, not the actual math — and
    /// also produced visibly incorrect output, so it was reverted entirely
    /// (see ScrubProxyCache's own class remarks). Every row of every frame
    /// is encoded and decoded on the CPU now.
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
    /// directly produces the long identical-byte runs a byte-level or
    /// general-purpose compressor handles best.
    ///
    /// SHARED/GLOBAL PALETTE (V6, DECIDED IN CONVERSATION) — every stored
    /// frame USED TO carry its own freshly-built 384-byte palette (v4/v5
    /// shape); as of format version 6, IndexedDelta7 files instead store
    /// ONE palette for the WHOLE FILE (built from a sample of frames across
    /// the source — see ScrubProxyCache.BuildAsync's sampling pass), kept
    /// once in the file's own layout (see ScrubProxyFormat's VERSION 6
    /// remarks) and read once by ScrubProxyReader at Open() time, exactly
    /// like the frame index table already is. This is a genuine ARCHITECTURE
    /// change to this codec's public surface, split into three pieces so
    /// each can be tested/reasoned about independently:
    ///   - AccumulateHistogram: the exact same per-pixel color-counting
    ///     loop Encode always ran internally, now exposed so a caller can
    ///     run it across MULTIPLE sampled frames into one shared histogram
    ///     before building a palette from it — nothing about the counting
    ///     logic itself changed, it's just no longer scoped to one frame.
    ///   - BuildPaletteFromHistogram: the exact same median-cut BuildPalette
    ///     + WritePalette pair Encode always ran internally, now exposed
    ///     directly so the caller can build ONE palette from the merged
    ///     multi-frame histogram instead of Encode building a fresh one
    ///     per frame.
    ///   - EncodeWithPalette: the exact same per-pixel PALETTE-vs-DELTA row
    ///     loop Encode always ran, factored out into the shared EncodeRows
    ///     helper so it can run against an EXTERNALLY SUPPLIED palette
    ///     (the shared one) instead of building its own. Encode (below)
    ///     still exists, unchanged in behavior, and still calls EncodeRows
    ///     too — it's simply no longer what ScrubProxyCache actually calls
    ///     for a new build; kept as a simple, still-correct, self-contained
    ///     one-shot entry point (useful for testing this codec against a
    ///     single frame in isolation, or for any future caller that
    ///     genuinely wants a fresh per-frame palette again).
    /// Decode itself needed ZERO changes for this — it already took an
    /// explicit `palette` parameter rather than assuming a per-frame one,
    /// so handing it the shared palette (read once, reused for every
    /// frame) instead of a freshly-read per-frame one is exactly what its
    /// existing signature was already built to support.
    ///
    /// THE DEFAULT PIXEL FORMAT (see EditSharpConfig.ScrubProxyPixelFormat)
    /// — CONFIRMED ON REAL HARDWARE to deliver a dramatic size reduction
    /// with correct behavior and good visual quality. The earlier Indexed8
    /// format it replaced as the default was removed entirely (decided in
    /// conversation: unnecessary complexity once IndexedDelta7 proved
    /// better). See ScrubProxyPixelFormat.IndexedDelta7's own remarks for
    /// the on-disk shape this feeds and EditSharpConfig for the build-time
    /// knob.
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
        /// * 4` interleaved bytes, no row padding; alpha is read but never
        /// used, see class remarks) into a FRESH, THIS-FRAME-ONLY 128-entry
        /// RGB palette
        /// (`paletteOut`, exactly Delta7PaletteByteSize bytes) and one
        /// control byte per pixel (`pixelsOut`, exactly `width * height`
        /// bytes). See class remarks, SHARED/GLOBAL PALETTE (V6) — this
        /// method is NOT what ScrubProxyCache actually calls for a new
        /// build any more (that's AccumulateHistogram + BuildPaletteFromHistogram
        /// + EncodeWithPalette instead), but is kept as a simple, still-
        /// correct, self-contained one-shot entry point.
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

            var histogram = new Dictionary<uint, int>();
            AccumulateHistogram(source, width, height, histogram);

            (uint Color, int Count)[] palette = BuildPalette(histogram);
            WritePalette(palette, paletteOut);

            EncodeRows(source, width, height, paletteOut, pixelsOut);
        }

        /// <summary>
        /// Counts every pixel's (R,G,B) color in `source` (one frame, same
        /// shape as Encode's own `source`) into `histogram`, ADDING to
        /// whatever counts it already holds rather than replacing them —
        /// see class remarks, SHARED/GLOBAL PALETTE (V6). Calling this
        /// once per sampled frame (against the SAME dictionary instance)
        /// is exactly how ScrubProxyCache.BuildAsync's sampling pass builds
        /// a histogram representative of the whole source, not just one
        /// frame, before calling BuildPaletteFromHistogram on the result.
        /// Alpha is read but never used, same as everywhere else in this
        /// codec (see class remarks on why this format carries no alpha).
        /// </summary>
        public static void AccumulateHistogram(
            ReadOnlySpan<byte> source, int width, int height, Dictionary<uint, int> histogram)
        {
            int pixelCount = width * height;
            if (source.Length != pixelCount * 4)
                throw new ArgumentException(
                    $"source must be exactly {pixelCount * 4} bytes for a {width}x{height} RGBA8888 frame, got {source.Length}.",
                    nameof(source));

            for (int i = 0; i < source.Length; i += 4)
            {
                uint key = PackRgb(source[i], source[i + 1], source[i + 2]);
                histogram[key] = histogram.TryGetValue(key, out int count) ? count + 1 : 1;
            }
        }

        /// <summary>
        /// Median-cuts `histogram` (built by one or more AccumulateHistogram
        /// calls) down to a 128-entry RGB palette and writes it to
        /// `paletteOut` — the exact same BuildPalette+WritePalette pair
        /// Encode always ran internally, just exposed directly so
        /// ScrubProxyCache can build ONE shared palette from a multi-frame
        /// histogram instead of Encode building a fresh one per frame. See
        /// class remarks, SHARED/GLOBAL PALETTE (V6).
        /// </summary>
        public static void BuildPaletteFromHistogram(Dictionary<uint, int> histogram, Span<byte> paletteOut)
        {
            if (paletteOut.Length != ScrubProxyFormat.Delta7PaletteByteSize)
                throw new ArgumentException(
                    $"paletteOut must be exactly {ScrubProxyFormat.Delta7PaletteByteSize} bytes.", nameof(paletteOut));

            (uint Color, int Count)[] palette = BuildPalette(histogram);
            WritePalette(palette, paletteOut);
        }

        /// <summary>
        /// Encodes one frame's control bytes AGAINST AN EXTERNALLY SUPPLIED
        /// palette (`palette`, exactly Delta7PaletteByteSize bytes — the
        /// shared/global palette read from, or about to be written to, the
        /// file's own header-level palette section) rather than building a
        /// fresh one from this frame alone. See class remarks, SHARED/GLOBAL
        /// PALETTE (V6) — this is what ScrubProxyCache.EncodeFramePixels
        /// actually calls for every frame of a new IndexedDelta7 build.
        /// </summary>
        public static void EncodeWithPalette(
            ReadOnlySpan<byte> source, int width, int height, ReadOnlySpan<byte> palette, Span<byte> pixelsOut)
        {
            int pixelCount = width * height;

            if (source.Length != pixelCount * 4)
                throw new ArgumentException(
                    $"source must be exactly {pixelCount * 4} bytes for a {width}x{height} RGBA8888 frame, got {source.Length}.",
                    nameof(source));
            if (palette.Length != ScrubProxyFormat.Delta7PaletteByteSize)
                throw new ArgumentException(
                    $"palette must be exactly {ScrubProxyFormat.Delta7PaletteByteSize} bytes.", nameof(palette));

            EncodeRows(source, width, height, palette, pixelsOut);
        }

        /// <summary>
        /// THE per-pixel PALETTE-vs-DELTA row loop — shared by Encode (after
        /// it builds its own per-frame palette) and EncodeWithPalette (given
        /// an external one). `palette` is always exactly Delta7PaletteByteSize
        /// raw RGB triples; length/shape validation is the caller's job
        /// (both public entry points already do it) so this method can stay
        /// a plain, allocation-light inner loop.
        /// </summary>
        private static void EncodeRows(
            ReadOnlySpan<byte> source, int width, int height, ReadOnlySpan<byte> palette, Span<byte> pixelsOut)
        {
            int pixelCount = width * height;
            if (pixelsOut.Length != pixelCount)
                throw new ArgumentException($"pixelsOut must be exactly {pixelCount} bytes.", nameof(pixelsOut));

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
                        int paletteOffset = idx * 3;
                        nearest = (idx, palette[paletteOffset], palette[paletteOffset + 1], palette[paletteOffset + 2]);
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
        /// read-side counterpart to Encode/EncodeWithPalette, used by
        /// ScrubProxyReader.GetFrameAt. `palette` is whatever palette this
        /// file actually uses for every frame — as of V6, the ONE shared/
        /// global palette read once at Open() time (see class remarks,
        /// SHARED/GLOBAL PALETTE (V6)) — this method needed no change at
        /// all for that: it always took an explicit palette parameter
        /// rather than assuming a per-frame one. Alpha is always written
        /// as 255 (opaque) — see class remarks on why this format carries
        /// no alpha information at all.
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
        /// actual channel value — called by BOTH the encode-side search
        /// (via FindBestDelta) and Decode, so the two can never compute the
        /// delta math differently. See class remarks, THE ENCODER MUST
        /// MIRROR THE DECODER'S OWN RECONSTRUCTED STATE.
        ///
        /// A GPU re-implementation of this exact clamp/offset formula was
        /// tried once (ScrubProxyGpuEncoder, since deleted — see
        /// ScrubProxyCache's own class remarks on why) but is gone now;
        /// this CPU version is the only place this math runs.
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
        /// buckets exist or every bucket is down to one distinct color.
        /// UNCHANGED FOR V6 — the only thing that changed is WHICH
        /// histogram gets handed in (one frame's worth, vs. several sampled
        /// frames' worth accumulated together — see AccumulateHistogram);
        /// this method has no notion of "how many frames" at all, it just
        /// median-cuts whatever counts the histogram already holds.
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

        /// <summary>
        /// Nearest palette entry to (r,g,b), reading directly from the raw
        /// on-disk RGB-triple byte layout (`palette`, exactly
        /// Delta7PaletteByteSize bytes) rather than an intermediate
        /// (uint Color, int Count)[] array — CHANGED FOR V6: EncodeRows is
        /// now shared between Encode (which still builds its own tuple-
        /// array palette internally, then writes it to bytes via
        /// WritePalette before calling this) and EncodeWithPalette (which
        /// only ever HAS the byte-array shape, since that's what's actually
        /// stored on disk/passed around at the ScrubProxyCache layer) — so
        /// this method reads bytes directly rather than requiring every
        /// caller to first unpack them back into a tuple array just to
        /// look a color up.
        /// </summary>
        private static byte FindNearestPaletteIndex(ReadOnlySpan<byte> palette, int r, int g, int b)
        {
            byte best = 0;
            long bestDistance = long.MaxValue;
            int count = palette.Length / 3;

            for (int i = 0; i < count; i++)
            {
                int offset = i * 3;
                long distance = DistanceSquared(r, g, b, palette[offset], palette[offset + 1], palette[offset + 2]);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = (byte)i;
                }
            }

            return best;
        }

        /// <summary>
        /// Redmean-style perceptually-weighted squared distance on RGB.
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