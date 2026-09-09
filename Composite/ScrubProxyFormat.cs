using System;

namespace EditSharp.Composite
{
    /// <summary>
    /// Which lossless transform, if any, every stored frame's bytes went
    /// through before being written to disk. A FILE-WIDE choice, not
    /// per-frame — recorded once in the fixed header and applied
    /// consistently to every frame in that file. See ScrubProxyRle for the
    /// actual Rle codec and EditSharpConfig.ScrubProxyCompressionScheme for
    /// the build-time default.
    ///
    /// APPLIES TO Indexed8/IndexedDelta7 FRAMES TOO (see
    /// ScrubProxyPixelFormat below) — specifically to those formats'
    /// per-pixel control-byte stream only, never to their embedded palette
    /// (see ScrubProxyPixelFormat's own remarks for why the palette is
    /// always stored raw).
    /// </summary>
    public enum ScrubProxyCompressionScheme
    {
        /// <summary>Raw bytes, no transform at all — the original v1 file shape.</summary>
        None = 0,

        /// <summary>
        /// PackBits-style byte-level run-length encoding (see
        /// ScrubProxyRle). Confirmed, via real-world testing in a separate
        /// application, to cost negligible CPU even on a hot per-tick
        /// decode path, in exchange for a real, often substantial
        /// reduction in a scrub proxy's on-disk size.
        /// </summary>
        Rle = 1,
    }

    /// <summary>
    /// How a stored frame's pixels are laid out on disk — a FILE-WIDE
    /// choice (recorded once in the fixed header), never per-frame, exactly
    /// like ScrubProxyCompressionScheme above. See EditSharpConfig.
    /// ScrubProxyPixelFormat for the build-time default, ColorQuantizer for
    /// Indexed8's quantization/dithering machinery, and IndexedDelta7Codec
    /// for IndexedDelta7's own encode/decode machinery.
    /// </summary>
    public enum ScrubProxyPixelFormat
    {
        /// <summary>
        /// Every stored frame is `width * height * 4` interleaved RGBA8888
        /// bytes, no row padding — the original v1/v2 shape, and still
        /// exactly what SkSourceDecoder's own raw pipe produces, so a
        /// Rgba8888-format frame drops straight into an SKImage with zero
        /// conversion once decompressed (see ScrubProxyRle's own remarks
        /// for the CompressionScheme.Rle case).
        /// </summary>
        Rgba8888 = 0,

        /// <summary>
        /// PALETTE QUANTIZATION — DECIDED IN CONVERSATION, ADDED AS A
        /// SECOND FORMAT ALONGSIDE Rgba8888 RATHER THAN REPLACING IT
        /// (per explicit user direction: "introducing a new format to the
        /// enum is a good idea"). Every stored frame is:
        ///   [Palette]  256 * 4 bytes — 256 RGBA8888 colors, ALWAYS STORED
        ///              RAW/UNCOMPRESSED regardless of the file's
        ///              CompressionScheme. Deliberately not RLE'd: 1024
        ///              bytes is already negligible next to a real frame's
        ///              index-byte stream, and a palette's own byte
        ///              sequence (256 arbitrary, usually non-repeating
        ///              colors) is exactly the kind of content PackBits-
        ///              style RLE compresses worst, so RLE'ing it would be
        ///              pure overhead with no realistic benefit.
        ///   [Indices]  width * height bytes — one byte per pixel, each an
        ///              index (0-255) into the palette above. THIS part IS
        ///              subject to the file's own CompressionScheme (Rle or
        ///              None), exactly like an Rgba8888 frame's own raw
        ///              bytes — a single flat index plane compresses at
        ///              least as well as raw RGBA under PackBits (often far
        ///              better: flat-colour/letterboxed regions collapse to
        ///              long runs of one repeated index byte instead of one
        ///              repeated 4-byte RGBA quad).
        /// A frame's TOTAL on-disk length (palette + stored index bytes) is
        /// what the file's frame index (see ScrubProxyFormat's own LAYOUT
        /// remarks) records — the palette/index SPLIT within that blob is
        /// always at the fixed 1024-byte boundary, so no extra per-frame
        /// metadata is needed to find it.
        ///
        /// FULL RGBA (4D), NOT RGB-ONLY QUANTIZATION — per explicit user
        /// direction, since scrub proxies participate in real alpha
        /// compositing (see ScrubFrameSource/SkFrameCompositor) and an
        /// RGB-only palette would flatten every source's alpha to a single
        /// binary in/out state. See ColorQuantizer for the actual median-
        /// cut-in-4D-space algorithm, its redmean-style (RGB) + weighted
        /// alpha distance metric, and its ordered/Bayer dithering pass
        /// (approved explicitly: "yes, let's implement the banding
        /// reduction").
        ///
        /// LOSSY, NAMED NOT HIDDEN: unlike Rgba8888 (a lossless, exact
        /// resample of the source at the proxy's own fixed resolution/rate),
        /// Indexed8 additionally quantizes each frame's colour space down
        /// to (at most) 256 distinct colours, chosen per-frame by
        /// ColorQuantizer's median-cut pass — real color error is possible,
        /// mitigated (not eliminated) by ordered dithering. Exactly the
        /// same "accurate to the proxy, not the source" trade-off this
        /// whole scrub-proxy mechanism already makes on resolution/frame
        /// rate (see ScrubProxyFormat's own class remarks) — this format
        /// just trades some of the SAME kind of accuracy for a large
        /// additional size reduction, which is what makes pushing proxy
        /// resolution up towards native affordable at all.
        /// </summary>
        Indexed8 = 1,

        /// <summary>
        /// HYBRID PALETTE + PREDICTIVE-DELTA ENCODING — DECIDED IN
        /// CONVERSATION, DIRECT IMPLEMENTATION OF A USER-PROPOSED
        /// PSEUDOCODE DESIGN, ADDED AS A THIRD FORMAT ALONGSIDE Rgba8888/
        /// Indexed8 RATHER THAN REPLACING EITHER (same "new enum value,
        /// nothing removed" pattern Indexed8 itself used). See
        /// IndexedDelta7Codec for the full encode/decode machinery and
        /// design reasoning — summarized here for the on-disk shape:
        ///
        /// Every stored frame is:
        ///   [Palette]  128 * 3 bytes (RGB, NOT RGBA — see below) — 128
        ///              colors, ALWAYS STORED RAW/UNCOMPRESSED, same
        ///              reasoning as Indexed8's own palette (negligible
        ///              size, compresses poorly under RLE anyway).
        ///   [Pixels]   width * height bytes — one CONTROL byte per pixel:
        ///              its top bit selects PALETTE mode (a 7-bit index
        ///              into the palette above, 0-127) or DELTA mode (a
        ///              small signed modulation of the pixel immediately
        ///              to its LEFT, split 2/3/2 bits across R/G/B — green
        ///              gets the most bits since the eye is most sensitive
        ///              to it). Whichever mode lands closer to the real
        ///              source pixel is chosen, per pixel. THIS part is
        ///              subject to the file's own CompressionScheme (Rle
        ///              or None), exactly like Indexed8's own index bytes.
        /// A frame's TOTAL on-disk length (palette + stored pixel bytes) is
        /// recorded in the file's frame index exactly like every other
        /// format — the palette/pixel SPLIT is always at the fixed
        /// Delta7PaletteByteSize (384-byte) boundary.
        ///
        /// RGB-ONLY (NOT RGBA) — UNLIKE Indexed8, DELIBERATELY: this
        /// format's byte budget (1 mode bit, 7 remaining bits) has no room
        /// left for an alpha term at all, and it doesn't need one — this
        /// format is used EXCLUSIVELY for Video-type MediaSourceNode
        /// frames (Image/Text input nodes never go through a scrub proxy
        /// at all — see ScrubFrameSource), which SkSourceDecoder's raw
        /// pipe always decodes fully opaque. Decoded frames always carry
        /// A=255.
        ///
        /// NO DITHERING — UNLIKE Indexed8, DELIBERATELY: Indexed8's ordered
        /// (Bayer) dithering exists to fake extra perceived color depth out
        /// of a fixed palette by deliberately varying neighboring pixels'
        /// chosen index, which is exactly what makes a dithered index plane
        /// compress poorly under RLE (see ScrubProxyRle's own remarks on
        /// the production bug a dither-like repeating pattern caused). This
        /// format's DELTA mode already gives an exact, non-dithered escape
        /// hatch for a pixel that's close to but not exactly a palette
        /// color — a real improvement in both directions at once: more
        /// accurate than a dithered approximation, AND far more
        /// RLE-friendly (flat and smoothly-gradient regions now tend to
        /// produce long runs of identical or near-identical control bytes,
        /// rather than a deliberately noisy dither pattern).
        ///
        /// LOSSY, NAMED NOT HIDDEN, SAME TRADE-OFF FAMILY AS Indexed8: this
        /// format additionally quantizes color the same general way
        /// Indexed8 does (a palette AND, here, a bounded per-pixel delta
        /// range), for the same reason — trading some of the same "accurate
        /// to the proxy, not the source" accuracy this whole mechanism
        /// already trades on resolution/frame rate for a further size
        /// reduction (and, per the user's own stated goal, a quality
        /// improvement in exchange too — see IndexedDelta7Codec's own
        /// remarks for the mechanism).
        ///
        /// EXPERIMENTAL — NOT THE DEFAULT: EditSharpConfig.
        /// ScrubProxyPixelFormat still defaults to Indexed8 (proven, real-
        /// hardware-confirmed); this format is available as an opt-in value
        /// pending its own real-hardware evaluation — see
        /// IndexedDelta7Codec's own remarks on why its exact step-size
        /// constants are flagged as unvalidated starting points.
        /// </summary>
        IndexedDelta7 = 2,
    }

    /// <summary>
    /// The on-disk shape of a ".esrp" (EditSharp Raw Proxy) file — a scrub
    /// proxy built by ScrubProxyCache and read by ScrubProxyReader. Shared
    /// between the two so the write side and the read side can never drift
    /// out of agreement about the header layout.
    ///
    /// WHY A CUSTOM RAW FORMAT, NOT ANOTHER ffmpeg-DECODED PROXY (decided in
    /// conversation, replacing the earlier I-frame-only ffmpeg-decode
    /// approach entirely — see Playback and ScrubFrameSource's own remarks):
    /// scrubbing needs "jump to an arbitrary position, instantly, with zero
    /// per-tick decode." A small intra-only ffmpeg-encoded proxy still needs
    /// a real decoder (process spawn, stream probing, however cheap) on
    /// every tick. This format needs none of that at all — every stored
    /// frame is a fixed-rate sample of pixels (RGBA8888 directly, or
    /// Indexed8/IndexedDelta7 — see ScrubProxyPixelFormat), so "read the
    /// frame nearest this timestamp" is a frame-index lookup (see LAYOUT
    /// below) followed by one pread-style read (see ScrubProxyReader, which
    /// uses System.IO.RandomAccess so concurrent/rapid seeks never contend
    /// on a shared stream position or spawn anything). No child process, no
    /// video-codec decode, no GOP/keyframe concept at all — which is also
    /// what lets the real forward-playback GPU decoder stay alive and
    /// undisturbed for the whole time a scrub session is active (see
    /// Playback's own remarks). Indexed8/IndexedDelta7's own per-frame
    /// expansion back to RGBA (see ScrubProxyReader.GetFrameAt) is a cheap
    /// in-memory lookup/small-delta-arithmetic pass, not a decode in this
    /// sense at all.
    ///
    /// FIXED SAMPLE RATE, NOT THE SOURCE'S OWN KEYFRAME SPACING: unlike the
    /// old keyframe-snapped approach, this format's frame density is a
    /// property of HOW THE PROXY WAS BUILT (EditSharpConfig.
    /// ScrubProxySampleRate), completely decoupled from the source's own
    /// GOP structure — a source with keyframes several seconds apart still
    /// gets fine-grained scrub steps, because the proxy was built by
    /// resampling to a constant rate up front (see ScrubProxyCache.BuildAsync
    /// using SkSourceDecoder's own fps-conform machinery), not by extracting
    /// the source's existing keyframes.
    ///
    /// PIXEL FORMAT IS RECORDED EXPLICITLY, NOT ASSUMED (see
    /// ScrubProxyPixelFormat) — a decoded frame always ultimately becomes
    /// RGBA8888 with no row padding by the time ScrubProxyReader.GetFrameAt
    /// returns it (matches SkSourceDecoder's own raw pipe format exactly,
    /// so an SKImage needs zero further conversion), but HOW that RGBA8888
    /// is actually stored on disk now varies by PixelFormat — see
    /// ScrubProxyPixelFormat's own remarks for the Indexed8/IndexedDelta7
    /// on-disk shapes.
    ///
    /// VERSION 2 — METADATA EMBEDDED, PER-FRAME RANDOM ACCESS VIA A FRAME
    /// INDEX, OPTIONAL COMPRESSION (all decided in conversation, once the
    /// v1 all-raw, two-file design had been proven correct and stable on
    /// real hardware):
    ///   - NO MORE .meta.json COMPANION FILE. Every fact a lookup used to
    ///     get from the separate meta file (SourceHash, KnownSourcePaths,
    ///     original dimensions, build-time target short side, created-at)
    ///     is now a small UTF8 JSON blob embedded directly in THIS file,
    ///     right after the fixed header (see ScrubProxyMeta/
    ///     ScrubProxyMetaSerializer) — one file per proxy instead of two,
    ///     and no more chance of the pair going out of sync with each
    ///     other on disk (a copy/move that only takes the .esrp and leaves
    ///     the .meta.json behind, or vice versa, used to silently break a
    ///     cache hit; that failure mode is now structurally impossible).
    ///   - A FRAME INDEX TABLE (FrameCount entries of absolute byte offset
    ///     + byte length) replaces v1's pure `HeaderSize + index *
    ///     frameByteSize` arithmetic. This is what makes per-frame
    ///     compression possible at all without breaking O(1) random
    ///     access: a compressed frame's size varies frame to frame, so
    ///     GetFrameAt can no longer compute an offset by multiplication —
    ///     it looks up this frame's own (offset, length) instead. The
    ///     whole table is small enough to read once, in full, when a
    ///     ScrubProxyReader is opened, and kept in memory for the reader's
    ///     lifetime — one extra small read per SOURCE (not per tick), not
    ///     per GetFrameAt call. Written for EVERY file regardless of
    ///     CompressionScheme (even None/raw, where every entry's Length is
    ///     the same constant) — one code path for both cases, at a fixed,
    ///     small, per-frame cost (12 bytes) that's negligible next to real
    ///     frame data. UNCHANGED BY Indexed8/IndexedDelta7: those formats'
    ///     frames simply vary in length by a different amount (palette +
    ///     compressed-or-not control bytes, rather than compressed-or-not
    ///     raw RGBA), the table itself doesn't care why a frame's length
    ///     varies.
    ///   - CompressionScheme (see the enum above) records which transform,
    ///     if any, every frame's stored bytes went through — see
    ///     ScrubProxyRle for the actual codec. A FILE-WIDE choice, not
    ///     per-frame: simpler to reason about, and PackBits-style RLE's
    ///     worst-case expansion on incompressible content is small and
    ///     bounded (see ScrubProxyRle's own remarks), so there's no real
    ///     pathological case that would need a per-frame raw-fallback
    ///     escape hatch.
    ///
    /// VERSION 3 — Indexed8 PIXEL FORMAT (decided in conversation, see
    /// ScrubProxyPixelFormat's own remarks for the full reasoning): the
    /// header's pixelFormat field, previously always written/validated as
    /// the single hardcoded Rgba8888 value, became a real, validated
    /// ScrubProxyPixelFormat discriminator a file can carry either value
    /// of.
    ///
    /// VERSION 4 — IndexedDelta7 PIXEL FORMAT (decided in conversation, see
    /// ScrubProxyPixelFormat.IndexedDelta7's own remarks and
    /// IndexedDelta7Codec for the full reasoning): the header's
    /// pixelFormat field's valid/interpreted range widened again to
    /// include this third value. Bumped for the exact same reason v3 was —
    /// the MEANING of "how many bytes does frame N's own slot decode into,
    /// and how" now depends on this field in a way a v3-or-earlier reader
    /// never accounted for, so an old reader must not silently misinterpret
    /// a new IndexedDelta7 file's frame bytes as Indexed8 or raw/RLE'd
    /// RGBA8888 — hence the version bump forces the same "treat as a miss
    /// and rebuild" fallback v3's own bump did. The header's own BYTE SIZE
    /// is unchanged (still 40 bytes) — only the legal/interpreted range of
    /// the existing pixelFormat field changed, same as last time.
    ///
    /// LAYOUT, IN ORDER:
    ///   [FixedHeader]  HeaderSize (40) bytes — see WriteHeader/ReadHeader.
    ///   [MetaBlob]     MetaBlobLength bytes, UTF8 JSON (ScrubProxyMeta).
    ///   [FrameIndex]   FrameCount * FrameIndexEntrySize (12) bytes.
    ///   [FrameData]    Every frame's stored bytes, back to back, at
    ///                  exactly the offsets/lengths the FrameIndex records
    ///                  (in practice sequential/contiguous, since the
    ///                  writer emits them in order, but a reader must
    ///                  trust the recorded offset, not assume that). For
    ///                  PixelFormat.Indexed8, each frame's own blob is
    ///                  itself [256*4-byte raw palette][index bytes]; for
    ///                  PixelFormat.IndexedDelta7, [128*3-byte raw
    ///                  palette][control bytes] — see ScrubProxyPixelFormat's
    ///                  own remarks for both.
    /// </summary>
    internal static class ScrubProxyFormat
    {
        /// <summary>ASCII "ESRP", read/written as a little-endian uint32.</summary>
        public const uint Magic = 0x50525345;

        public const int CurrentVersion = 4;

        /// <summary>256-color palette, 4 bytes (RGBA8888) each — see ScrubProxyPixelFormat.Indexed8.</summary>
        public const int IndexedPaletteEntryCount = 256;
        public const int IndexedPaletteByteSize = IndexedPaletteEntryCount * 4;

        /// <summary>
        /// 128-color palette, 3 bytes (RGB, no alpha) each — see
        /// ScrubProxyPixelFormat.IndexedDelta7 and IndexedDelta7Codec for
        /// why 128 (not 256) entries and why RGB-only.
        /// </summary>
        public const int Delta7PaletteEntryCount = 128;
        public const int Delta7PaletteByteSize = Delta7PaletteEntryCount * 3;

        /// <summary>
        /// magic(4) + version(4) + width(4) + height(4) + pixelFormat(4) +
        /// compressionScheme(4) + sampleRate(8, double) + frameCount(4) +
        /// metaBlobLength(4) = 40 bytes, fixed for every file this format
        /// version writes.
        /// </summary>
        public const int HeaderSize = 40;

        /// <summary>One FrameIndex entry: absolute file offset (long, 8) + byte length (int, 4).</summary>
        public const int FrameIndexEntrySize = 12;

        public static void WriteHeader(
            Span<byte> destination, int width, int height, ScrubProxyPixelFormat pixelFormat,
            ScrubProxyCompressionScheme compressionScheme, double sampleRate, int frameCount, int metaBlobLength)
        {
            if (destination.Length < HeaderSize)
                throw new ArgumentException($"Destination must be at least {HeaderSize} bytes.", nameof(destination));

            BitConverter.TryWriteBytes(destination[0..4], Magic);
            BitConverter.TryWriteBytes(destination[4..8], CurrentVersion);
            BitConverter.TryWriteBytes(destination[8..12], width);
            BitConverter.TryWriteBytes(destination[12..16], height);
            BitConverter.TryWriteBytes(destination[16..20], (int)pixelFormat);
            BitConverter.TryWriteBytes(destination[20..24], (int)compressionScheme);
            BitConverter.TryWriteBytes(destination[24..32], sampleRate);
            BitConverter.TryWriteBytes(destination[32..36], frameCount);
            BitConverter.TryWriteBytes(destination[36..40], metaBlobLength);
        }

        public readonly record struct Header(
            int Width, int Height, ScrubProxyPixelFormat PixelFormat, ScrubProxyCompressionScheme CompressionScheme,
            double SampleRate, int FrameCount, int MetaBlobLength);

        public static Header ReadHeader(ReadOnlySpan<byte> source, string diagnosticPath)
        {
            if (source.Length < HeaderSize)
                throw new InvalidDataException(
                    $"'{diagnosticPath}' is too short to be a valid scrub proxy (expected at least " +
                    $"{HeaderSize} header bytes, got {source.Length}).");

            uint magic = BitConverter.ToUInt32(source[0..4]);
            if (magic != Magic)
                throw new InvalidDataException(
                    $"'{diagnosticPath}' does not look like an EditSharp scrub proxy (bad magic).");

            int version = BitConverter.ToInt32(source[4..8]);
            if (version != CurrentVersion)
                throw new InvalidDataException(
                    $"'{diagnosticPath}' is scrub-proxy format version {version}, this build only reads " +
                    $"version {CurrentVersion} — treat as a miss and rebuild.");

            int width = BitConverter.ToInt32(source[8..12]);
            int height = BitConverter.ToInt32(source[12..16]);
            int pixelFormatRaw = BitConverter.ToInt32(source[16..20]);
            int compressionSchemeRaw = BitConverter.ToInt32(source[20..24]);
            double sampleRate = BitConverter.ToDouble(source[24..32]);
            int frameCount = BitConverter.ToInt32(source[32..36]);
            int metaBlobLength = BitConverter.ToInt32(source[36..40]);

            if (pixelFormatRaw != (int)ScrubProxyPixelFormat.Rgba8888 &&
                pixelFormatRaw != (int)ScrubProxyPixelFormat.Indexed8 &&
                pixelFormatRaw != (int)ScrubProxyPixelFormat.IndexedDelta7)
                throw new InvalidDataException(
                    $"'{diagnosticPath}' uses scrub-proxy pixel format {pixelFormatRaw}, this build only " +
                    $"reads {(int)ScrubProxyPixelFormat.Rgba8888} (Rgba8888)/{(int)ScrubProxyPixelFormat.Indexed8} " +
                    $"(Indexed8)/{(int)ScrubProxyPixelFormat.IndexedDelta7} (IndexedDelta7) — treat as a miss " +
                    "and rebuild.");

            if (compressionSchemeRaw != (int)ScrubProxyCompressionScheme.None &&
                compressionSchemeRaw != (int)ScrubProxyCompressionScheme.Rle)
                throw new InvalidDataException(
                    $"'{diagnosticPath}' uses scrub-proxy compression scheme {compressionSchemeRaw}, this " +
                    "build only reads None(0)/Rle(1) — treat as a miss and rebuild.");

            if (width <= 0 || height <= 0 || frameCount <= 0 || sampleRate <= 0 || metaBlobLength < 0)
                throw new InvalidDataException($"'{diagnosticPath}' has an invalid scrub-proxy header.");

            return new Header(width, height, (ScrubProxyPixelFormat)pixelFormatRaw,
                (ScrubProxyCompressionScheme)compressionSchemeRaw, sampleRate, frameCount, metaBlobLength);
        }

        /// <summary>The embedded metadata blob always starts immediately after the fixed header.</summary>
        public static long MetaBlobOffset => HeaderSize;

        /// <summary>The frame index table always starts immediately after the metadata blob.</summary>
        public static long FrameIndexOffset(int metaBlobLength) => HeaderSize + metaBlobLength;

        /// <summary>Frame 0's data starts immediately after the frame index table.</summary>
        public static long FrameDataStartOffset(int metaBlobLength, int frameCount) =>
            FrameIndexOffset(metaBlobLength) + (long)frameCount * FrameIndexEntrySize;

        public static void WriteFrameIndexEntry(Span<byte> destination, long offset, int length)
        {
            if (destination.Length < FrameIndexEntrySize)
                throw new ArgumentException(
                    $"Destination must be at least {FrameIndexEntrySize} bytes.", nameof(destination));

            BitConverter.TryWriteBytes(destination[0..8], offset);
            BitConverter.TryWriteBytes(destination[8..12], length);
        }

        public static (long Offset, int Length) ReadFrameIndexEntry(ReadOnlySpan<byte> source)
        {
            long offset = BitConverter.ToInt64(source[0..8]);
            int length = BitConverter.ToInt32(source[8..12]);
            return (offset, length);
        }
    }
}