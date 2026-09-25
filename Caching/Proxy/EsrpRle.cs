using System;

namespace EditSharp.Caching.Proxy
{
    /// <summary>
    /// A simple, fast, lossless PackBits-style byte-level run-length codec —
    /// the EsrpCompressionScheme.Rle scheme (see EsrpFormat).
    /// DECIDED IN CONVERSATION after real-world testing (in a separate
    /// application) showed this exact style of RLE costs negligible CPU
    /// even on a hot per-tick decode path, in exchange for a real, often
    /// substantial reduction in a scrub proxy's on-disk size — flat colour,
    /// letterboxing/pillarboxing, and gradient-heavy footage in particular
    /// compress well; genuinely noisy footage compresses little, but never
    /// pathologically expands (see the worst-case note below — and see BUG
    /// FIXED IN THE FIELD below for a real case where an earlier version of
    /// this codec DID expand pathologically). Deliberately NOT a general-
    /// purpose/entropy coder (no Huffman/arithmetic stage, nothing
    /// dictionary-based) — the whole point is that this stays cheap enough
    /// to run on every single scrub tick's decode, not just at build time.
    ///
    /// FORMAT (classic PackBits, applied to an arbitrary interleaved byte
    /// stream — no per-channel/per-pixel structure assumed, this is pure
    /// byte-level compression, used both for raw RGBA8888 frame bytes and,
    /// as of EsrpPixelFormat.Indexed8, for a frame's separate index-
    /// byte plane): a sequence of packets, each starting with one signed
    /// control byte `n`:
    ///   n in [0, 127]    -&gt; the next (n + 1) bytes are LITERAL, copy as-is.
    ///   n in [-127, -1]  -&gt; the next ONE byte is REPEATED (1 - n) times.
    ///   n == -128        -&gt; no-op (never emitted by Encode, tolerated on
    ///                       decode for robustness against a hand-crafted
    ///                       or future encoder).
    ///
    /// BUG FIXED IN THE FIELD (real crash, confirmed on real hardware
    /// against real Indexed8 index-plane data): Encode used to treat ANY
    /// run of 2+ identical bytes as worth its own repeat packet
    /// (`runLength >= 2`), with the literal-accumulation loop stopping
    /// early the instant it saw even a 2-byte run starting. That is
    /// classic PackBits' OWN documented threshold, and it is WRONG for
    /// buffer-sizing purposes: a repeat packet costs exactly 2 bytes
    /// (control + value) to represent ANY run length from 1 to 128, which
    /// only pays for itself once the run is at least 3 bytes long (a run
    /// of 2 costs the same 2 bytes as encoding those same 2 bytes as part
    /// of a literal run would). Worse, breaking a literal run early to
    /// emit a 2-byte repeat ALSO forces a fresh (at-least 2-byte) literal
    /// packet on whatever precedes it — a real, reproducible input shape
    /// (repeating 3-byte groups of [distinct byte, then a pair of one
    /// repeated byte] — exactly the kind of pattern a quantized per-pixel
    /// index plane can plausibly contain) drove this into a genuine,
    /// measured 33% size EXPANSION (4 output bytes for every 3 input
    /// bytes: a 2-byte literal-of-1 packet immediately followed by a
    /// 2-byte repeat-of-2 packet), blowing straight through the buffer
    /// this class pre-allocates for the ~0.8%-overhead case the class
    /// remarks originally (and, for the OLD threshold, wrongly) documented
    /// — surfaced in production as `ArgumentException: Destination is too
    /// short` out of the `raw.Slice(...).CopyTo(...)` call inside Encode.
    /// FIX, two independent layers:
    ///   1. MinRepeatRun raised to 3 — see the constant's own remarks for
    ///      why this restores the standard PackBits worst-case bound (a
    ///      run must be at least 3 bytes before it's worth its own
    ///      packet), which is the actual root-cause fix.
    ///   2. Encode's output buffer is now DYNAMICALLY GROWN (doubling,
    ///      like a plain growable buffer) rather than sized once up front
    ///      from a fixed worst-case formula and trusted not to overflow —
    ///      see GrowIfNeeded below. This is deliberate defense in depth:
    ///      fix #1 alone would have been enough on its own to restore the
    ///      documented ~0.8% bound, but a fixed pre-sized buffer failing
    ///      silently (well, loudly, but confusingly) in production once
    ///      already is reason enough to remove reliance on any single
    ///      static worst-case proof for a codec that has to be correct
    ///      against arbitrary real-world content it can't fully predict.
    ///
    /// WORST-CASE EXPANSION IS SMALL AND BOUNDED (true again, now that
    /// MinRepeatRun is 3 — see that constant's remarks): fully
    /// incompressible input costs exactly one control byte per 128 literal
    /// bytes (~0.8% overhead) — nowhere near enough to justify a per-frame
    /// raw-fallback escape hatch (see EsrpFormat's own remarks on why
    /// CompressionScheme is a file-wide choice, not per-frame). This bound
    /// is now enforced structurally by #2 above regardless, not just
    /// trusted by proof.
    /// </summary>
    internal static class EsrpRle
    {
        private const int MaxLiteralRun = 128;
        private const int MaxRepeatRun = 128;

        /// <summary>
        /// The shortest run of identical bytes actually worth encoding as
        /// its own repeat packet — see class remarks, BUG FIXED IN THE
        /// FIELD. A repeat packet always costs exactly 2 bytes (control +
        /// value), so it only pays for itself once it replaces 3+ bytes of
        /// what would otherwise be literal data; encoding a run of exactly
        /// 2 as its OWN packet costs the same 2 bytes a literal encoding of
        /// those same 2 bytes would, while additionally forcing whatever
        /// literal run precedes/follows it to end early — pure overhead
        /// with no compensating benefit. Runs of 1-2 identical bytes are
        /// now simply left as part of whatever literal run they fall
        /// inside (see the lookahead check in Encode's literal-
        /// accumulation loop below), exactly like any other non-repeating
        /// bytes.
        /// </summary>
        private const int MinRepeatRun = 3;

        /// <summary>
        /// Encodes `raw` into a fresh, exactly-sized (trimmed) buffer. The
        /// caller decides what to do with the result — ProxyCache.
        /// EncodeFramePixels writes it straight to the frame's own slot in
        /// the .esrp file, recording the actual returned length in the
        /// frame index (see EsrpFormat's LAYOUT remarks).
        /// </summary>
        public static byte[] Encode(ReadOnlySpan<byte> raw)
        {
            // Starting capacity matches the PROVEN worst case for
            // MinRepeatRun >= 3 (raw.Length + ceil(raw.Length / 128)
            // control bytes — see class remarks) — but GrowIfNeeded below
            // is what actually GUARANTEES correctness now, not this
            // estimate; see class remarks, BUG FIXED IN THE FIELD, fix #2.
            byte[] output = new byte[raw.Length + (raw.Length + MaxLiteralRun - 1) / MaxLiteralRun + 1];
            int outPos = 0;
            int i = 0;

            while (i < raw.Length)
            {
                int runLength = 1;
                while (i + runLength < raw.Length &&
                       raw[i + runLength] == raw[i] &&
                       runLength < MaxRepeatRun)
                {
                    runLength++;
                }

                if (runLength >= MinRepeatRun)
                {
                    // Repeat packet: control = 1 - runLength, in [-127, -1].
                    GrowIfNeeded(ref output, outPos, 2);
                    output[outPos++] = unchecked((byte)(1 - runLength));
                    output[outPos++] = raw[i];
                    i += runLength;
                    continue;
                }

                // No run of MinRepeatRun+ starts here — accumulate a
                // literal run up to MaxLiteralRun bytes, stopping early
                // the moment a real (MinRepeatRun+) repeat run starts so
                // the outer loop can pick it up as its own packet next
                // iteration. A run of 1-2 identical bytes encountered
                // along the way does NOT stop this loop — see MinRepeatRun's
                // own remarks — it's simply absorbed as ordinary literal
                // bytes.
                int literalStart = i;
                int literalLength = 1;
                i++;

                while (i < raw.Length && literalLength < MaxLiteralRun)
                {
                    if (StartsRunOfAtLeast(raw, i, MinRepeatRun)) break;

                    literalLength++;
                    i++;
                }

                GrowIfNeeded(ref output, outPos, 1 + literalLength);
                output[outPos++] = unchecked((byte)(literalLength - 1));
                raw.Slice(literalStart, literalLength).CopyTo(output.AsSpan(outPos));
                outPos += literalLength;
            }

            return outPos == output.Length ? output : output.AsSpan(0, outPos).ToArray();
        }

        /// <summary>
        /// True if the run of identical bytes starting at `raw[start]` is
        /// at least `minLength` long. Bounded to exactly `minLength`
        /// comparisons (never scans further) since the caller only ever
        /// needs a yes/no answer against the fixed MinRepeatRun threshold,
        /// not the run's own true length — the OUTER loop's own runLength
        /// computation (above) is what actually measures a real repeat's
        /// full extent once one is confirmed to start.
        /// </summary>
        private static bool StartsRunOfAtLeast(ReadOnlySpan<byte> raw, int start, int minLength)
        {
            if (start + minLength > raw.Length) return false;

            byte value = raw[start];
            for (int offset = 1; offset < minLength; offset++)
            {
                if (raw[start + offset] != value) return false;
            }

            return true;
        }

        /// <summary>
        /// Doubles `buffer` (preserving its first `used` bytes) if fewer
        /// than `needed` bytes remain past `used` — see class remarks, BUG
        /// FIXED IN THE FIELD, fix #2. This is what makes Encode correct
        /// for ANY input regardless of whether the up-front capacity
        /// estimate above turns out to be exact, an overestimate, or (as
        /// happened in the field, under the old MinRepeatRun=2 threshold)
        /// an underestimate — a future change to this codec's packing
        /// heuristic can no longer reintroduce a silent buffer overflow.
        /// </summary>
        private static void GrowIfNeeded(ref byte[] buffer, int used, int needed)
        {
            if (buffer.Length - used >= needed) return;

            int newLength = Math.Max(buffer.Length * 2, used + needed);
            var grown = new byte[newLength];
            buffer.AsSpan(0, used).CopyTo(grown);
            buffer = grown;
        }

        /// <summary>
        /// Decodes `compressed` into `destination`, which must be exactly
        /// the known decompressed size — EsrpReader always knows
        /// this ahead of time (either Width * Height * 4 for an Rgba8888
        /// frame, or Width * Height for an Indexed8 frame's index plane —
        /// see EsrpPixelFormat), so there's no need to grow a buffer
        /// here. Throws if `compressed` decodes to more or fewer bytes than
        /// `destination`'s length, or would read/write past either buffer
        /// mid-packet — either means a corrupt/truncated file or a
        /// version/scheme mismatch that slipped past ReadHeader. Unaffected
        /// by the MinRepeatRun change above: a repeat packet's own encoded
        /// length (1 - control) can be anything from 1 to 128 as far as
        /// this method is concerned — Encode simply never happens to emit
        /// one below 3 any more.
        /// </summary>
        public static void Decode(ReadOnlySpan<byte> compressed, Span<byte> destination)
        {
            int inPos = 0;
            int outPos = 0;

            while (inPos < compressed.Length)
            {
                sbyte control = unchecked((sbyte)compressed[inPos++]);

                if (control == -128) continue; // no-op packet, tolerated

                if (control >= 0)
                {
                    int literalLength = control + 1;
                    if (outPos + literalLength > destination.Length || inPos + literalLength > compressed.Length)
                        throw new InvalidOperationException(
                            "EsrpRle.Decode: literal packet overruns the destination/source buffer — " +
                            "the compressed frame data is corrupt or truncated.");

                    compressed.Slice(inPos, literalLength).CopyTo(destination.Slice(outPos));
                    inPos += literalLength;
                    outPos += literalLength;
                }
                else
                {
                    int repeatLength = 1 - control;
                    if (outPos + repeatLength > destination.Length || inPos >= compressed.Length)
                        throw new InvalidOperationException(
                            "EsrpRle.Decode: repeat packet overruns the destination/source buffer — " +
                            "the compressed frame data is corrupt or truncated.");

                    byte value = compressed[inPos++];
                    destination.Slice(outPos, repeatLength).Fill(value);
                    outPos += repeatLength;
                }
            }

            if (outPos != destination.Length)
                throw new InvalidOperationException(
                    $"EsrpRle.Decode: decoded {outPos} bytes, expected exactly {destination.Length} — " +
                    "the compressed frame data is corrupt, truncated, or was encoded for a different frame size.");
        }
    }
}