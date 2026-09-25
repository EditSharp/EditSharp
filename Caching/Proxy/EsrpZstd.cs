using System;
using ZstdSharp;

namespace EditSharp.Caching.Proxy
{
    /// <summary>Zstandard compression of .esrp frame planes, through the fully managed ZstdSharp.Port.</summary>
    /// <remarks>
    /// Each call compresses one frame's plane on its own, never against another
    /// frame, so any frame can be read without its neighbours. Zstd decodes at
    /// the same speed whatever level encoded it, so a slow, high level at build
    /// time costs nothing when scrubbing. The shared palette of an IndexedDelta7
    /// file is stored uncompressed.
    /// </remarks>
    internal static class EsrpZstd
    {
        //a compressor holds sizeable state at high levels; reuse one per thread, per level
        [ThreadStatic] private static Compressor? _compressor;
        [ThreadStatic] private static int _compressorLevel;

        //compresses at EditSharpConfig.EsrpCompressionLevel into a new, exactly sized buffer
        public static byte[] Encode(ReadOnlySpan<byte> raw)
        {
            int level = EditSharpConfig.EsrpCompressionLevel;

            if (_compressor is null || _compressorLevel != level)
            {
                _compressor?.Dispose();
                _compressor = new Compressor(level);
                _compressorLevel = level;
            }

            return _compressor.Wrap(raw).ToArray();
        }

        //decompresses into `destination`, which must be exactly the plane's size; a different size means a corrupt file
        public static void Decode(ReadOnlySpan<byte> compressed, Span<byte> destination)
        {
            using var decompressor = new Decompressor();
            int written = decompressor.Unwrap(compressed, destination);

            if (written != destination.Length)
                throw new InvalidOperationException(
                    $"EsrpZstd.Decode: decoded {written} bytes, expected exactly " +
                    $"{destination.Length}; the compressed frame data is corrupt, truncated, or was " +
                    "encoded for a different frame size.");
        }
    }
}