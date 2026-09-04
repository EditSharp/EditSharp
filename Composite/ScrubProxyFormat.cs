using System;

namespace EditSharp.Composite
{
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
    /// frame is raw, fixed-size, uncompressed RGBA8888 pixels at a FIXED
    /// stored sample rate, so "read the frame nearest this timestamp" is
    /// pure arithmetic (`offset = HeaderSize + frameIndex * frameByteSize`)
    /// followed by one pread-style read (see ScrubProxyReader, which uses
    /// System.IO.RandomAccess so concurrent/rapid seeks never contend on a
    /// shared stream position or spawn anything). No child process, no
    /// decode, no GOP/keyframe concept at all — which is also what lets the
    /// real forward-playback GPU decoder stay alive and undisturbed for the
    /// whole time a scrub session is active (see Playback's own remarks).
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
    /// PIXEL FORMAT IS ALWAYS RGBA8888, WITH NO ROW PADDING (rowBytes ==
    /// width * 4) — matches SkSourceDecoder's own raw pipe format exactly,
    /// so every stored frame drops straight into an SKImage with zero
    /// conversion on read. PixelFormat is still stored explicitly (not
    /// assumed) so a later format revision has somewhere to signal a
    /// different encoding without silently misreading old files as the new
    /// shape.
    /// </summary>
    internal static class ScrubProxyFormat
    {
        /// <summary>ASCII "ESRP", read/written as a little-endian uint32.</summary>
        public const uint Magic = 0x50525345;

        public const int CurrentVersion = 1;

        public const int PixelFormatRgba8888 = 0;

        /// <summary>
        /// magic(4) + version(4) + width(4) + height(4) + pixelFormat(4) +
        /// sampleRate(8, double) + frameCount(4) = 32 bytes, fixed for every
        /// file this format ever writes.
        /// </summary>
        public const int HeaderSize = 32;

        public static void WriteHeader(Span<byte> destination, int width, int height, double sampleRate, int frameCount)
        {
            if (destination.Length < HeaderSize)
                throw new ArgumentException($"Destination must be at least {HeaderSize} bytes.", nameof(destination));

            BitConverter.TryWriteBytes(destination[0..4], Magic);
            BitConverter.TryWriteBytes(destination[4..8], CurrentVersion);
            BitConverter.TryWriteBytes(destination[8..12], width);
            BitConverter.TryWriteBytes(destination[12..16], height);
            BitConverter.TryWriteBytes(destination[16..20], PixelFormatRgba8888);
            BitConverter.TryWriteBytes(destination[20..28], sampleRate);
            BitConverter.TryWriteBytes(destination[28..32], frameCount);
        }

        public readonly record struct Header(int Width, int Height, int PixelFormat, double SampleRate, int FrameCount);

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
            int pixelFormat = BitConverter.ToInt32(source[16..20]);
            double sampleRate = BitConverter.ToDouble(source[20..28]);
            int frameCount = BitConverter.ToInt32(source[28..32]);

            if (pixelFormat != PixelFormatRgba8888)
                throw new InvalidDataException(
                    $"'{diagnosticPath}' uses scrub-proxy pixel format {pixelFormat}, this build only " +
                    $"reads {PixelFormatRgba8888} (Rgba8888).");

            if (width <= 0 || height <= 0 || frameCount <= 0 || sampleRate <= 0)
                throw new InvalidDataException($"'{diagnosticPath}' has an invalid scrub-proxy header.");

            return new Header(width, height, pixelFormat, sampleRate, frameCount);
        }
    }
}