using System;
using System.Collections.Generic;

namespace EditSharp.Caching.Proxy
{
    /// <summary>The IndexedDelta7 pixel format: palette building and decoding. <see cref="IndexedDelta7Encoder"/> encodes.</summary>
    /// <remarks>
    /// Each pixel is one byte. With the top bit clear, the low 7 bits index the
    /// file's shared 128-colour RGB palette. With it set, the byte is a small
    /// change from the pixel to its left: 2 bits of red level, 3 of green and 2
    /// of blue (green gets the most because the eye is most sensitive to it),
    /// each a signed level scaled by RStep, GStep or BStep. The first pixel of a
    /// row is always a palette index, so every row decodes on its own. Deltas
    /// apply to the colour the decoder reconstructed for the left pixel, never
    /// the source's, so encoder and decoder can't drift apart; <see cref="ApplyDelta"/>
    /// is the one place that math happens. There is no alpha: video proxies are
    /// opaque, and decoding writes 255. The palette is built once per proxy by
    /// median cut over colours counted from sampled frames.
    /// </remarks>
    internal static class IndexedDelta7Codec
    {
        private const int PaletteSize = EsrpFormat.Delta7PaletteEntryCount; // 128

        private const byte ModeBit = 0x80;
        private const byte PaletteIndexMask = 0x7F;

        private const int RLevelBits = 2, GLevelBits = 3, BLevelBits = 2;
        private const int RLevelCount = 1 << RLevelBits; // 4
        private const int GLevelCount = 1 << GLevelBits; // 8
        private const int BLevelCount = 1 << BLevelBits; // 4

        private const int ZeroRLevel = RLevelCount / 2; // 2 -> level 0
        private const int ZeroGLevel = GLevelCount / 2; // 4 -> level 0
        private const int ZeroBLevel = BLevelCount / 2; // 2 -> level 0

        //how far one delta level moves each channel; chosen by reasoning about proxy footage, not measured
        private const int RStep = 6;
        private const int GStep = 4;
        private const int BStep = 6;

        //adds every pixel's RGB colour in one frame to `histogram`; called once per sampled frame
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

        //median-cuts `histogram` down to the 128-entry palette and writes it to `paletteOut`
        public static void BuildPaletteFromHistogram(Dictionary<uint, int> histogram, Span<byte> paletteOut)
        {
            if (paletteOut.Length != EsrpFormat.Delta7PaletteByteSize)
                throw new ArgumentException(
                    $"paletteOut must be exactly {EsrpFormat.Delta7PaletteByteSize} bytes.", nameof(paletteOut));

            (uint Color, int Count)[] palette = BuildPalette(histogram);
            WritePalette(palette, paletteOut);
        }

        //the signed change each delta code makes to each channel, so decoding
        //a delta is three table reads and three clamps
        private static readonly int[] DeltaR = new int[256];
        private static readonly int[] DeltaG = new int[256];
        private static readonly int[] DeltaB = new int[256];

        static IndexedDelta7Codec()
        {
            for (int code = ModeBit; code < 256; code++)
            {
                DeltaR[code] = (((code >> (GLevelBits + BLevelBits)) & (RLevelCount - 1)) - ZeroRLevel) * RStep;
                DeltaG[code] = (((code >> BLevelBits) & (GLevelCount - 1)) - ZeroGLevel) * GStep;
                DeltaB[code] = ((code & (BLevelCount - 1)) - ZeroBLevel) * BStep;
            }
        }

        //expands one frame to RGBA8888 against the file's palette; alpha is always 255.
        //rows are independent, so they decode on every core at once
        public static void Decode(
            ReadOnlySpan<byte> palette, ReadOnlySpan<byte> pixels, int width, int height, Span<byte> destination)
        {
            int pixelCount = width * height;

            if (palette.Length != EsrpFormat.Delta7PaletteByteSize)
                throw new ArgumentException(
                    $"palette must be exactly {EsrpFormat.Delta7PaletteByteSize} bytes.", nameof(palette));
            if (pixels.Length != pixelCount)
                throw new ArgumentException($"pixels must be exactly {pixelCount} bytes.", nameof(pixels));
            if (destination.Length != pixelCount * 4)
                throw new ArgumentException(
                    $"destination must be exactly {pixelCount * 4} bytes for {pixelCount} pixels.", nameof(destination));

            //the palette as packed little-endian RGBA, one read per pixel
            uint[] rgba = new uint[PaletteSize];
            for (int i = 0; i < PaletteSize; i++)
                rgba[i] = palette[i * 3] | ((uint)palette[i * 3 + 1] << 8) | ((uint)palette[i * 3 + 2] << 16) | 0xFF000000u;

            //spans can't cross into the parallel body: the buffers are pinned for its lifetime
            unsafe
            {
                fixed (byte* codes = pixels)
                fixed (byte* output = destination)
                fixed (uint* paletteRgba = rgba)
                {
                    nint codesAt = (nint)codes, outputAt = (nint)output, paletteAt = (nint)paletteRgba;
                    int workers = Math.Clamp(Environment.ProcessorCount, 1, Math.Max(1, height / 8));

                    System.Threading.Tasks.Parallel.For(0, workers, worker =>
                    {
                        int from = (int)((long)height * worker / workers);
                        int to = (int)((long)height * (worker + 1) / workers);
                        DecodeRows((byte*)codesAt, (uint*)outputAt, (uint*)paletteAt, width, from, to);
                    });
                }
            }
        }

        private static unsafe void DecodeRows(byte* codes, uint* output, uint* paletteRgba, int width, int fromRow, int toRow)
        {
            for (int y = fromRow; y < toRow; y++)
            {
                byte* row = codes + (long)y * width;
                uint* dest = output + (long)y * width;
                int r = 0, g = 0, b = 0;

                for (int x = 0; x < width; x++)
                {
                    byte code = row[x];

                    if ((code & ModeBit) == 0)
                    {
                        uint packed = paletteRgba[code];
                        r = (int)(packed & 0xFF);
                        g = (int)((packed >> 8) & 0xFF);
                        b = (int)((packed >> 16) & 0xFF);
                        dest[x] = packed;
                        continue;
                    }

                    r = Math.Clamp(r + DeltaR[code], 0, 255);
                    g = Math.Clamp(g + DeltaG[code], 0, 255);
                    b = Math.Clamp(b + DeltaB[code], 0, 255);
                    dest[x] = (uint)r | ((uint)g << 8) | ((uint)b << 16) | 0xFF000000u;
                }
            }
        }

        /// <summary>The left pixel's colour moved by signed levels; the only place delta math happens, for encoding and decoding alike.</summary>
        private static void ApplyDelta(
            byte prevR, byte prevG, byte prevB, int rLevel, int gLevel, int bLevel,
            out byte r, out byte g, out byte b)
        {
            r = (byte)Math.Clamp(prevR + rLevel * RStep, 0, 255);
            g = (byte)Math.Clamp(prevG + gLevel * GStep, 0, 255);
            b = (byte)Math.Clamp(prevB + bLevel * BStep, 0, 255);
        }

        //median cut in RGB space until there are 128 buckets or every bucket is one colour
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