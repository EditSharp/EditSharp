using System;

namespace EditSharp.Caching.Proxy
{
    /// <summary>Encodes frames to IndexedDelta7 against one shared palette, for a whole proxy build.</summary>
    /// <remarks>
    /// Each pixel becomes whichever of the nearest palette colour or the closest
    /// delta from its left neighbour is nearer the source under the redmean
    /// distance, ties going to the palette. The nearest palette entry for each
    /// colour is remembered for the whole build (a 16 MiB table, filled on first
    /// sight), and the delta search picks green and blue separately for each red
    /// level: 48 distance checks per pixel instead of 128. Frames may be encoded
    /// on several threads at once.
    /// </remarks>
    internal sealed class IndexedDelta7Encoder
    {
        private const byte ModeBit = 0x80;
        private const byte Unknown = 0xFF;

        //mirrors IndexedDelta7Codec's level layout and step sizes
        private const int RLevels = 4, GLevels = 8, BLevels = 4;
        private const int ZeroR = 2, ZeroG = 4, ZeroB = 2;
        private const int RStep = 6, GStep = 4, BStep = 6;
        private const int GShift = 2, RShift = 5;

        private readonly byte[] _palette;
        private readonly byte[] _nearest = new byte[1 << 24];

        public IndexedDelta7Encoder(ReadOnlySpan<byte> palette)
        {
            if (palette.Length != EsrpFormat.Delta7PaletteByteSize)
                throw new ArgumentException($"palette must be exactly {EsrpFormat.Delta7PaletteByteSize} bytes.", nameof(palette));

            _palette = palette.ToArray();
            Array.Fill(_nearest, Unknown);
        }

        public void Encode(ReadOnlySpan<byte> source, int width, int height, Span<byte> codes)
        {
            int pixelCount = width * height;
            if (source.Length != pixelCount * 4)
                throw new ArgumentException($"source must be exactly {pixelCount * 4} bytes for a {width}x{height} RGBA8888 frame.", nameof(source));
            if (codes.Length != pixelCount)
                throw new ArgumentException($"codes must be exactly {pixelCount} bytes.", nameof(codes));

            ReadOnlySpan<byte> palette = _palette;

            for (int y = 0; y < height; y++)
            {
                int prevR = 0, prevG = 0, prevB = 0;

                for (int x = 0; x < width; x++)
                {
                    int s = (y * width + x) * 4;
                    int r = source[s], g = source[s + 1], b = source[s + 2];

                    int index = Nearest(r, g, b);
                    int pr = palette[index * 3], pg = palette[index * 3 + 1], pb = palette[index * 3 + 2];

                    byte code = (byte)index;
                    int cr = pr, cg = pg, cb = pb;

                    //the first pixel of a row has no left neighbour to be a delta of
                    if (x > 0)
                    {
                        long paletteDistance = Distance(r, g, b, pr, pg, pb);

                        if (paletteDistance > 0)
                        {
                            (int deltaCode, int dr, int dg, int db, long deltaDistance) = BestDelta(prevR, prevG, prevB, r, g, b);

                            //ties go to the palette
                            if (deltaDistance < paletteDistance)
                            {
                                code = (byte)(ModeBit | deltaCode);
                                cr = dr; cg = dg; cb = db;
                            }
                        }
                    }

                    codes[y * width + x] = code;

                    //the decoder continues from what was chosen, not from the source pixel
                    prevR = cr; prevG = cg; prevB = cb;
                }
            }
        }

        private int Nearest(int r, int g, int b)
        {
            int key = (r << 16) | (g << 8) | b;
            byte known = _nearest[key];
            if (known != Unknown) return known;

            ReadOnlySpan<byte> palette = _palette;
            int best = 0;
            long bestDistance = long.MaxValue;

            for (int i = 0; i < EsrpFormat.Delta7PaletteEntryCount; i++)
            {
                long distance = Distance(r, g, b, palette[i * 3], palette[i * 3 + 1], palette[i * 3 + 2]);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            //a racing thread computes and stores the same byte
            _nearest[key] = (byte)best;
            return best;
        }

        //the delta code closest to the target, first in (r, g, b) level order among equals;
        //with the red level fixed the distance's weights are too, so green and blue are chosen independently
        private static (int Code, int R, int G, int B, long Distance) BestDelta(int prevR, int prevG, int prevB, int r, int g, int b)
        {
            if (prevR == r && prevG == g && prevB == b)
                return ((ZeroR << RShift) | (ZeroG << GShift) | ZeroB, prevR, prevG, prevB, 0);

            int bestCode = 0, bestR = prevR, bestG = prevG, bestB = prevB;
            long bestDistance = long.MaxValue;

            for (int rl = 0; rl < RLevels; rl++)
            {
                int cr = Math.Clamp(prevR + (rl - ZeroR) * RStep, 0, 255);
                long rMean = (r + cr) / 2;
                long dr = r - cr;
                long weightB = 767 - rMean;
                long redTerm = (512 + rMean) * dr * dr;

                int gl = 0, cg = 0;
                long greenTerm = long.MaxValue;
                for (int level = 0; level < GLevels; level++)
                {
                    int candidate = Math.Clamp(prevG + (level - ZeroG) * GStep, 0, 255);
                    long d = g - candidate;
                    long term = 1024 * d * d;
                    if (term < greenTerm) { greenTerm = term; gl = level; cg = candidate; }
                }

                int bl = 0, cb = 0;
                long blueTerm = long.MaxValue;
                for (int level = 0; level < BLevels; level++)
                {
                    int candidate = Math.Clamp(prevB + (level - ZeroB) * BStep, 0, 255);
                    long d = b - candidate;
                    long term = weightB * d * d;
                    if (term < blueTerm) { blueTerm = term; bl = level; cb = candidate; }
                }

                long distance = redTerm + greenTerm + blueTerm;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestCode = (rl << RShift) | (gl << GShift) | bl;
                    bestR = cr; bestG = cg; bestB = cb;
                }
            }

            return (bestCode, bestR, bestG, bestB, bestDistance);
        }

        //the "redmean" weighted distance IndexedDelta7Codec uses
        private static long Distance(int r1, int g1, int b1, int r2, int g2, int b2)
        {
            long rMean = (r1 + r2) / 2;
            long dr = r1 - r2, dg = g1 - g2, db = b1 - b2;
            return (512 + rMean) * dr * dr + 1024 * dg * dg + (767 - rMean) * db * db;
        }
    }
}
