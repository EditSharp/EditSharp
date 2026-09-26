using System;
using System.IO;

namespace EditSharp.Caching.Proxy
{
    /// <summary>
    /// Which lossless transform every stored frame's bytes went through; a
    /// file-wide choice recorded in the header. Applies to an IndexedDelta7
    /// file's control-byte planes, never to its shared palette. See EsrpZstd.
    /// </summary>
    public enum EsrpCompressionScheme
    {
        /// <summary>Raw bytes, no transform.</summary>
        None = 0,

        /// <summary>Zstandard via ZstdSharp.Port (fully managed). Compresses each frame on its own, so random access stays O(1).</summary>
        Zstd = 1,
    }

    /// <summary>How a stored frame's pixels are laid out; file-wide, recorded in the header.</summary>
    public enum EsrpPixelFormat
    {
        /// <summary>width * height * 4 interleaved RGBA8888 bytes; exactly what SourceDecoder produces.</summary>
        Rgba8888 = 0,

        /// <summary>
        /// A 7-bit index/delta control byte per pixel against ONE palette
        /// shared by the whole file (built from a sampling pass before
        /// encoding). See IndexedDelta7Codec.
        /// </summary>
        IndexedDelta7 = 1,
    }

    /// <summary>The .esrp proxy file: a fixed header, an embedded JSON meta blob, an optional shared palette, a preallocated frame index, then frame data.</summary>
    /// <remarks>
    /// The file is readable while it's being written (since version 8); the frame rate is an exact fraction (since version 9). The index is preallocated for Capacity frames (the probed
    /// duration's worth) and zero-filled; the writer appends a frame's data,
    /// flushes, and only then fills that frame's index entry, so a non-zero
    /// entry always points at complete data. Frames are written strictly in
    /// order, so the readable part is always a prefix. When the build ends
    /// the writer sets the Complete flag and the ACTUAL frame count (the
    /// container's duration can overstate it); readers then know that any
    /// frame past FrameCount is past the end rather than still pending.
    /// <code>
    ///   [0..52)   header, little-endian:
    ///               magic u32, version i32, width i32, height i32,
    ///               pixelFormat i32, compression i32, frameRate i32 numerator + i32 denominator,
    ///               capacity i32, metaLength i32, paletteLength i32,
    ///               flags i32 (bit 0 = complete), frameCount i32
    ///   meta      UTF8 JSON (EsrpMeta)
    ///   palette   IndexedDelta7 only (Delta7PaletteByteSize bytes)
    ///   index     capacity * 12 bytes: offset i64, length i32 (0 = not written)
    ///   data      frame blobs, in frame order
    /// </code>
    /// </remarks>
    internal static class EsrpFormat
    {
        public const uint Magic = 0x50525345; // "ESRP"
        public const int CurrentVersion = 9;

        public const int Delta7PaletteEntryCount = 128;
        public const int Delta7PaletteByteSize = Delta7PaletteEntryCount * 3;

        public const int HeaderSize = 52;
        public const int FrameIndexEntrySize = 12;

        //the two header fields rewritten in place when a build completes
        public const int FlagsOffset = 44;
        public const int FrameCountOffset = 48;

        public const int CompleteFlag = 1;

        public readonly record struct Header(
            int Width, int Height, EsrpPixelFormat PixelFormat, EsrpCompressionScheme CompressionScheme,
            Rational FrameRate, int Capacity, int MetaLength, int PaletteLength, bool Complete, int FrameCount);

        public static void WriteHeader(Span<byte> destination, Header header)
        {
            if (destination.Length < HeaderSize)
                throw new ArgumentException($"Destination must be at least {HeaderSize} bytes.", nameof(destination));

            BitConverter.TryWriteBytes(destination[0..4], Magic);
            BitConverter.TryWriteBytes(destination[4..8], CurrentVersion);
            BitConverter.TryWriteBytes(destination[8..12], header.Width);
            BitConverter.TryWriteBytes(destination[12..16], header.Height);
            BitConverter.TryWriteBytes(destination[16..20], (int)header.PixelFormat);
            BitConverter.TryWriteBytes(destination[20..24], (int)header.CompressionScheme);
            BitConverter.TryWriteBytes(destination[24..28], checked((int)header.FrameRate.Num));
            BitConverter.TryWriteBytes(destination[28..32], checked((int)header.FrameRate.Den));
            BitConverter.TryWriteBytes(destination[32..36], header.Capacity);
            BitConverter.TryWriteBytes(destination[36..40], header.MetaLength);
            BitConverter.TryWriteBytes(destination[40..44], header.PaletteLength);
            BitConverter.TryWriteBytes(destination[44..48], header.Complete ? CompleteFlag : 0);
            BitConverter.TryWriteBytes(destination[48..52], header.FrameCount);
        }

        public static Header ReadHeader(ReadOnlySpan<byte> source, string diagnosticPath)
        {
            if (source.Length < HeaderSize)
                throw new InvalidDataException($"'{diagnosticPath}' is too short to be a proxy ({source.Length} header bytes).");

            if (BitConverter.ToUInt32(source[0..4]) != Magic)
                throw new InvalidDataException($"'{diagnosticPath}' is not an .esrp proxy (bad magic).");

            int version = BitConverter.ToInt32(source[4..8]);
            if (version != CurrentVersion)
                throw new InvalidDataException($"'{diagnosticPath}' is .esrp version {version}; this build reads {CurrentVersion}.");

            int rateDen = BitConverter.ToInt32(source[28..32]);
            if (rateDen <= 0)
                throw new InvalidDataException($"'{diagnosticPath}' has an invalid .esrp header.");

            var header = new Header(
                Width: BitConverter.ToInt32(source[8..12]),
                Height: BitConverter.ToInt32(source[12..16]),
                PixelFormat: (EsrpPixelFormat)BitConverter.ToInt32(source[16..20]),
                CompressionScheme: (EsrpCompressionScheme)BitConverter.ToInt32(source[20..24]),
                FrameRate: new Rational(BitConverter.ToInt32(source[24..28]), rateDen),
                Capacity: BitConverter.ToInt32(source[32..36]),
                MetaLength: BitConverter.ToInt32(source[36..40]),
                PaletteLength: BitConverter.ToInt32(source[40..44]),
                Complete: (BitConverter.ToInt32(source[44..48]) & CompleteFlag) != 0,
                FrameCount: BitConverter.ToInt32(source[48..52]));

            if (!Enum.IsDefined(header.PixelFormat) || !Enum.IsDefined(header.CompressionScheme))
                throw new InvalidDataException($"'{diagnosticPath}' uses a pixel format or compression this build can't read.");

            if (header.Width <= 0 || header.Height <= 0 || header.Capacity <= 0 || !header.FrameRate.IsPositive ||
                header.MetaLength < 0 || header.PaletteLength < 0 || header.FrameCount < 0 || header.FrameCount > header.Capacity)
                throw new InvalidDataException($"'{diagnosticPath}' has an invalid .esrp header.");

            return header;
        }

        public static long MetaOffset => HeaderSize;

        public static long PaletteOffset(int metaLength) => HeaderSize + metaLength;

        public static long IndexOffset(int metaLength, int paletteLength) => PaletteOffset(metaLength) + paletteLength;

        public static long DataStartOffset(int metaLength, int paletteLength, int capacity) =>
            IndexOffset(metaLength, paletteLength) + (long)capacity * FrameIndexEntrySize;

        public static void WriteIndexEntry(Span<byte> destination, long offset, int length)
        {
            BitConverter.TryWriteBytes(destination[0..8], offset);
            BitConverter.TryWriteBytes(destination[8..12], length);
        }

        public static (long Offset, int Length) ReadIndexEntry(ReadOnlySpan<byte> source) =>
            (BitConverter.ToInt64(source[0..8]), BitConverter.ToInt32(source[8..12]));
    }
}
