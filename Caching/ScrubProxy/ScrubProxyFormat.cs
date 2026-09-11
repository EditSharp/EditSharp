using System;

namespace EditSharp.Caching.ScrubProxy
{
    /// <summary>
    /// Which lossless transform, if any, every stored frame's bytes went
    /// through before being written to disk. A FILE-WIDE choice, not
    /// per-frame — recorded once in the fixed header and applied
    /// consistently to every frame in that file. See ScrubProxyZstd for the
    /// actual codec and EditSharpConfig.ScrubProxyCompressionScheme for the
    /// build-time default.
    ///
    /// APPLIES TO IndexedDelta7 FRAMES TOO (see ScrubProxyPixelFormat
    /// below) — specifically to that format's per-pixel control-byte
    /// stream only, never to its file-wide shared palette (always stored
    /// raw regardless — see ScrubProxyPixelFormat's own remarks).
    ///
    /// Rle (PackBits-style byte-level run-length encoding) was removed —
    /// decided in conversation: now that Zstd is the settled, better-
    /// performing default, keeping a strictly-worse legacy scheme around
    /// was unneeded complexity.
    /// </summary>
    public enum ScrubProxyCompressionScheme
    {
        /// <summary>Raw bytes, no transform at all — the original v1 file shape.</summary>
        None = 0,

        /// <summary>
        /// Zstandard compression via ZstdSharp.Port — a fully-managed,
        /// pure-C# port with no native/P-Invoke dependency (see
        /// ScrubProxyZstd for the full reasoning and wrapper). DECIDED IN
        /// CONVERSATION, direct response to a user request to revisit
        /// compression once IndexedDelta7 proved the pixel-encoding side
        /// still had real size headroom left, in the hope of freeing up
        /// further budget for higher proxy quality. CONFIRMED ON REAL
        /// HARDWARE, alongside IndexedDelta7, to compress meaningfully
        /// better than the earlier Rle scheme on the same input.
        ///
        /// A GENERAL-PURPOSE ENTROPY-CODING PASS ON TOP OF WHATEVER
        /// PIXEL FORMAT IS IN USE, NOT A REPLACEMENT FOR ONE: this scheme
        /// compresses whichever byte plane it's handed (raw RGBA bytes, or
        /// an IndexedDelta7 control-byte plane) — it composes with any
        /// ScrubProxyPixelFormat value, it doesn't compete with one.
        ///
        /// THE DEFAULT — see EditSharpConfig.ScrubProxyCompressionScheme:
        /// IndexedDelta7+Zstd is the settled, best-performing combination
        /// this cache builds.
        /// </summary>
        Zstd = 1,
    }

    /// <summary>
    /// How a stored frame's pixels are laid out on disk — a FILE-WIDE
    /// choice (recorded once in the fixed header), never per-frame, exactly
    /// like ScrubProxyCompressionScheme above. See EditSharpConfig.
    /// ScrubProxyPixelFormat for the build-time default and
    /// IndexedDelta7Codec for IndexedDelta7's own encode/decode machinery.
    ///
    /// Indexed8 (a per-frame 256-color palette + ordered dithering) was
    /// removed — decided in conversation: now that IndexedDelta7 is the
    /// settled, better-performing default, keeping a strictly-worse legacy
    /// format around was unneeded complexity.
    /// </summary>
    public enum ScrubProxyPixelFormat
    {
        /// <summary>
        /// Every stored frame is `width * height * 4` interleaved RGBA8888
        /// bytes, no row padding — the original v1/v2 shape, and still
        /// exactly what SourceDecoder's own raw pipe produces, so a
        /// Rgba8888-format frame drops straight into an SKImage with zero
        /// conversion once decompressed.
        /// </summary>
        Rgba8888 = 0,

        /// <summary>
        /// HYBRID PALETTE + PREDICTIVE-DELTA ENCODING — DECIDED IN
        /// CONVERSATION, DIRECT IMPLEMENTATION OF A USER-PROPOSED
        /// PSEUDOCODE DESIGN. See IndexedDelta7Codec for the full encode/
        /// decode machinery and design reasoning — summarized here for the
        /// on-disk shape:
        ///
        /// Every stored frame is:
        ///   [Pixels]   width * height bytes — one CONTROL byte per pixel:
        ///              its top bit selects PALETTE mode (a 7-bit index
        ///              into the file's shared palette, 0-127 — see SHARED/
        ///              GLOBAL PALETTE (V6) below) or DELTA mode (a small
        ///              signed modulation of the pixel immediately to its
        ///              LEFT, split 2/3/2 bits across R/G/B — green gets
        ///              the most bits since the eye is most sensitive to
        ///              it). Whichever mode lands closer to the real
        ///              source pixel is chosen, per pixel. THIS part is
        ///              subject to the file's own CompressionScheme
        ///              (Zstd or None).
        /// A frame's on-disk length (the compressed-or-not control-byte
        /// plane alone, as of V6 — see below) is recorded in the file's
        /// frame index exactly like every other format.
        ///
        /// SHARED/GLOBAL PALETTE (V6, DECIDED IN CONVERSATION) — direct
        /// response to the user's own real-hardware measurements showing
        /// IndexedDelta7+Zstd still short of a 720p30 size target, and to
        /// their explicit choice of "shared/global palette" as the next
        /// lever to pull. THROUGH V5, every stored frame carried its OWN
        /// freshly-built 128*3-byte palette (see the old Delta7PaletteByteSize-
        /// boundary split within each frame's blob) — recurring per-frame
        /// overhead that scales with frame count, and one that also let
        /// consecutive frames' palettes drift independently of each other
        /// even when the source's actual color content barely changed.
        /// AS OF V6: ScrubProxyCache.BuildAsync's own sampling pass (see its
        /// class remarks) builds ONE 128*3-byte palette from a bounded
        /// sample of frames spread across the WHOLE source, stores it
        /// EXACTLY ONCE in the file (see LAYOUT below — a new
        /// [GlobalPalette] section, sized by the header's own
        /// GlobalPaletteLength field, sitting between the metadata blob
        /// and the frame index table), and every frame's own on-disk blob
        /// becomes JUST its control-byte plane — no embedded palette at
        /// all any more. ScrubProxyReader reads this section once, at
        /// Open() time, exactly like the frame index table already is (see
        /// its own remarks) and reuses it for every GetFrameAt call for
        /// that source — IndexedDelta7Codec.Decode needed ZERO changes for
        /// this, since it already took an explicit `palette` parameter
        /// rather than assuming a per-frame one.
        ///
        /// RGB-ONLY (NOT RGBA) — this format's byte budget (1 mode bit, 7
        /// remaining bits) has no room left for an alpha term at all, and
        /// it doesn't need one — this format is used EXCLUSIVELY for
        /// Video-type VideoSourceNode frames (Image/Text input nodes never
        /// go through a scrub proxy at all — see ScrubFrameSource), which
        /// SourceDecoder's raw pipe always decodes fully opaque. Decoded
        /// frames always carry A=255.
        ///
        /// NO DITHERING: this format's DELTA mode already gives an exact,
        /// non-dithered escape hatch for a pixel that's close to but not
        /// exactly a palette color — more accurate than a dithered
        /// approximation, AND far more compression-friendly (flat and
        /// smoothly-gradient regions now tend to produce long runs of
        /// identical or near-identical control bytes, rather than a
        /// deliberately noisy dither pattern).
        ///
        /// LOSSY, NAMED NOT HIDDEN.
        ///
        /// THE DEFAULT — see EditSharpConfig.ScrubProxyPixelFormat:
        /// CONFIRMED ON REAL HARDWARE, alongside Zstd compression, to be
        /// the best-performing combination this cache builds.
        /// </summary>
        IndexedDelta7 = 1,
    }

    /// <summary>
    /// The on-disk shape of a ".esrp" (EditSharp Raw Proxy) file — a scrub
    /// proxy built by ScrubProxyCache and read by ScrubProxyReader. Shared
    /// between the two so the write side and the read side can never drift
    /// out of agreement about the header layout.
    ///
    /// WHY A CUSTOM RAW FORMAT, NOT ANOTHER ffmpeg-DECODED PROXY: scrubbing
    /// needs "jump to an arbitrary position, instantly, with zero per-tick
    /// decode." Every stored frame is a fixed-rate sample of pixels
    /// (RGBA8888 directly, or IndexedDelta7 — see ScrubProxyPixelFormat),
    /// so "read the frame nearest this timestamp" is a frame-index lookup
    /// (see LAYOUT below) followed by one pread-style read (see
    /// ScrubProxyReader, which uses System.IO.RandomAccess so concurrent/
    /// rapid seeks never contend on a shared stream position or spawn
    /// anything). No child process, no video-codec decode, no GOP/keyframe
    /// concept at all.
    ///
    /// FIXED SAMPLE RATE, NOT THE SOURCE'S OWN KEYFRAME SPACING: this
    /// format's frame density is a property of HOW THE PROXY WAS BUILT
    /// (EditSharpConfig.ScrubProxySampleRate), completely decoupled from
    /// the source's own GOP structure.
    ///
    /// VERSION 2 — METADATA EMBEDDED, PER-FRAME RANDOM ACCESS VIA A FRAME
    /// INDEX, OPTIONAL COMPRESSION. VERSION 4 — IndexedDelta7 PIXEL FORMAT.
    /// VERSION 5 — Zstd COMPRESSION SCHEME. (The header's own byte size was
    /// unchanged across those bumps, only the legal/interpreted range of an
    /// existing field changed each time.)
    ///
    /// VERSION 6 — IndexedDelta7 SHARED/GLOBAL PALETTE (decided in
    /// conversation, see ScrubProxyPixelFormat.IndexedDelta7's own SHARED/
    /// GLOBAL PALETTE (V6) remarks for the full reasoning): UNLIKE every
    /// previous bump, this one DOES change the header's own byte size —
    /// HeaderSize grows from 40 to 44 bytes, adding one new field,
    /// GlobalPaletteLength (int) — because this is the first change that
    /// alters the file's overall LAYOUT (a new section exists at all),
    /// not just the legal range of an existing field. GlobalPaletteLength
    /// is 0 for an Rgba8888 file (no shared-palette section exists for it
    /// at all) and Delta7PaletteByteSize (384) for an IndexedDelta7 file.
    ///
    /// VERSION 7 — Indexed8 PIXEL FORMAT AND Rle COMPRESSION SCHEME REMOVED
    /// (decided in conversation: once IndexedDelta7+Zstd was settled as the
    /// best-performing, default combination, the earlier Indexed8/Rle
    /// legacy options — and an experimental GPU-accelerated encode path for
    /// IndexedDelta7 — were unneeded complexity and removed). Both enums'
    /// remaining values were renumbered (ScrubProxyPixelFormat.IndexedDelta7
    /// 2->1, ScrubProxyCompressionScheme.Zstd 2->1) to close the gap left
    /// by the removed values, so — same as every previous version bump —
    /// the header's own byte layout is UNCHANGED (still 44 bytes); only the
    /// legal/interpreted range of the pixelFormat/compressionScheme fields
    /// changed. CurrentVersion is bumped specifically so a pre-V7 file's
    /// now-renumbered raw field values are never silently misread under
    /// the new numbering — same version-gate safety net every earlier bump
    /// relied on.
    ///
    /// LAYOUT, IN ORDER (V7):
    ///   [FixedHeader]     HeaderSize (44) bytes — see WriteHeader/ReadHeader.
    ///   [MetaBlob]        MetaBlobLength bytes, UTF8 JSON (ScrubProxyMeta).
    ///   [GlobalPalette]   GlobalPaletteLength bytes (0 for Rgba8888;
    ///                     Delta7PaletteByteSize for IndexedDelta7) — see
    ///                     VERSION 6 above.
    ///   [FrameIndex]      FrameCount * FrameIndexEntrySize (12) bytes.
    ///   [FrameData]       Every frame's stored bytes, back to back, at
    ///                     exactly the offsets/lengths the FrameIndex
    ///                     records. For PixelFormat.IndexedDelta7, a
    ///                     frame's own blob is JUST its control-byte plane
    ///                     — no embedded palette (that lives in the
    ///                     file-wide [GlobalPalette] section above).
    /// </summary>
    internal static class ScrubProxyFormat
    {
        /// <summary>ASCII "ESRP", read/written as a little-endian uint32.</summary>
        public const uint Magic = 0x50525345;

        public const int CurrentVersion = 7;

        /// <summary>
        /// 128-color palette, 3 bytes (RGB, no alpha) each — see
        /// ScrubProxyPixelFormat.IndexedDelta7 and IndexedDelta7Codec for
        /// why 128 (not 256) entries and why RGB-only. This is the size of
        /// the file-WIDE shared [GlobalPalette] section for an
        /// IndexedDelta7 file (see VERSION 6 remarks above).
        /// </summary>
        public const int Delta7PaletteEntryCount = 128;
        public const int Delta7PaletteByteSize = Delta7PaletteEntryCount * 3;

        /// <summary>
        /// magic(4) + version(4) + width(4) + height(4) + pixelFormat(4) +
        /// compressionScheme(4) + sampleRate(8, double) + frameCount(4) +
        /// metaBlobLength(4) + globalPaletteLength(4) = 44 bytes, fixed for
        /// every file this format version writes. Grew from 40 to 44 in
        /// V6 — see VERSION 6 remarks above.
        /// </summary>
        public const int HeaderSize = 44;

        /// <summary>One FrameIndex entry: absolute file offset (long, 8) + byte length (int, 4).</summary>
        public const int FrameIndexEntrySize = 12;

        public static void WriteHeader(
            Span<byte> destination, int width, int height, ScrubProxyPixelFormat pixelFormat,
            ScrubProxyCompressionScheme compressionScheme, double sampleRate, int frameCount, int metaBlobLength,
            int globalPaletteLength)
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
            BitConverter.TryWriteBytes(destination[40..44], globalPaletteLength);
        }

        public readonly record struct Header(
            int Width, int Height, ScrubProxyPixelFormat PixelFormat, ScrubProxyCompressionScheme CompressionScheme,
            double SampleRate, int FrameCount, int MetaBlobLength, int GlobalPaletteLength);

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
            int globalPaletteLength = BitConverter.ToInt32(source[40..44]);

            if (pixelFormatRaw != (int)ScrubProxyPixelFormat.Rgba8888 &&
                pixelFormatRaw != (int)ScrubProxyPixelFormat.IndexedDelta7)
                throw new InvalidDataException(
                    $"'{diagnosticPath}' uses scrub-proxy pixel format {pixelFormatRaw}, this build only " +
                    $"reads {(int)ScrubProxyPixelFormat.Rgba8888} (Rgba8888)/" +
                    $"{(int)ScrubProxyPixelFormat.IndexedDelta7} (IndexedDelta7) — treat as a miss and rebuild.");

            if (compressionSchemeRaw != (int)ScrubProxyCompressionScheme.None &&
                compressionSchemeRaw != (int)ScrubProxyCompressionScheme.Zstd)
                throw new InvalidDataException(
                    $"'{diagnosticPath}' uses scrub-proxy compression scheme {compressionSchemeRaw}, this " +
                    $"build only reads {(int)ScrubProxyCompressionScheme.None} (None)/" +
                    $"{(int)ScrubProxyCompressionScheme.Zstd} (Zstd) — treat as a miss and rebuild.");

            if (width <= 0 || height <= 0 || frameCount <= 0 || sampleRate <= 0 || metaBlobLength < 0 ||
                globalPaletteLength < 0)
                throw new InvalidDataException($"'{diagnosticPath}' has an invalid scrub-proxy header.");

            return new Header(width, height, (ScrubProxyPixelFormat)pixelFormatRaw,
                (ScrubProxyCompressionScheme)compressionSchemeRaw, sampleRate, frameCount, metaBlobLength,
                globalPaletteLength);
        }

        /// <summary>The embedded metadata blob always starts immediately after the fixed header.</summary>
        public static long MetaBlobOffset => HeaderSize;

        /// <summary>
        /// The file-wide shared-palette section (see VERSION 6 remarks)
        /// always starts immediately after the metadata blob — zero-length
        /// (nothing actually there) for a file whose GlobalPaletteLength is 0.
        /// </summary>
        public static long GlobalPaletteOffset(int metaBlobLength) => HeaderSize + metaBlobLength;

        /// <summary>The frame index table always starts immediately after the shared-palette section.</summary>
        public static long FrameIndexOffset(int metaBlobLength, int globalPaletteLength) =>
            GlobalPaletteOffset(metaBlobLength) + globalPaletteLength;

        /// <summary>Frame 0's data starts immediately after the frame index table.</summary>
        public static long FrameDataStartOffset(int metaBlobLength, int globalPaletteLength, int frameCount) =>
            FrameIndexOffset(metaBlobLength, globalPaletteLength) + (long)frameCount * FrameIndexEntrySize;

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