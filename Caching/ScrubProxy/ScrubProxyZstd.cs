using System;
using ZstdSharp;

namespace EditSharp.Caching.ScrubProxy
{
    /// <summary>
    /// A thin wrapper around ZstdSharp.Port (a fully-managed, pure-C# port
    /// of the Zstandard compression library — NO native/P-Invoke
    /// dependency, confirmed against the library's own GitHub source) —
    /// the ScrubProxyCompressionScheme.Zstd scheme (see ScrubProxyFormat).
    ///
    /// DECIDED IN CONVERSATION: the user asked to revisit scrub-proxy
    /// compression once IndexedDelta7 proved the pixel-encoding side had
    /// real headroom left, specifically to free up further size budget
    /// that could go toward higher proxy quality. Offered three options
    /// (Zstd via a pure-C# port, LZ4 via a pure-C# port, or a hand-rolled
    /// entropy coder) — the user chose Zstd, pure-C#. Zstd is now the
    /// ONLY compression scheme this cache builds (alongside None) — the
    /// earlier Rle scheme it replaced as the default was removed entirely
    /// once Zstd proved better (decided in conversation: unnecessary
    /// complexity to keep a strictly-worse legacy scheme around).
    ///
    /// WHY ZSTD SPECIFICALLY (the two reasons that drove the choice):
    ///   - Zstd's DECODE speed is roughly independent of the COMPRESSION
    ///     LEVEL used at encode time. This scrub-proxy pipeline only ever
    ///     compresses once, at build time (see ScrubProxyCache.BuildAsync),
    ///     but decompresses on every single scrub tick (see
    ///     ScrubProxyReader.GetFrameAt) — so an aggressive, slow, one-time
    ///     build-time compression level costs nothing extra per tick,
    ///     unlike a codec whose decode cost scales with how hard the
    ///     encoder worked.
    ///   - It's a mature, widely-deployed, heavily-fuzzed real-world codec,
    ///     not a new hand-rolled one. Reusing a hardened library carries
    ///     far less correctness risk than a hand-rolled entropy coder
    ///     would, for a component with no compiler available in this
    ///     environment to catch a mistake before it ships.
    ///
    /// WHY A WRAPPER CLASS AT ALL, RATHER THAN CALLING ZstdSharp DIRECTLY
    /// AT EACH CALL SITE: a simple Encode(ReadOnlySpan&lt;byte&gt;) -&gt; byte[] /
    /// Decode(ReadOnlySpan&lt;byte&gt;, Span&lt;byte&gt;) shape, so
    /// ScrubProxyCache.EncodeFramePixels and ScrubProxyReader's
    /// Read*Frame methods can dispatch across None/Zstd through one
    /// shared code path each (see those classes' own remarks) without
    /// caring which concrete codec is behind a given scheme value, and so
    /// this file is the ONLY place that needs to know ZstdSharp.Port's own
    /// API surface (Compressor/Decompressor construction, IDisposable
    /// lifetime, the Wrap/Unwrap method shapes) at all.
    ///
    /// APPLIES TO Rgba8888/IndexedDelta7 FRAMES ALIKE: this scheme
    /// compresses whichever byte plane it's handed (raw RGBA bytes, or an
    /// IndexedDelta7 control-byte plane) with zero awareness of what that
    /// plane represents — never the file's shared IndexedDelta7 palette,
    /// which stays raw (a palette's bytes are exactly the kind of content
    /// a general-purpose compressor gains the least from, at the cost of
    /// needless CPU on every read).
    ///
    /// STRICTLY INTRA-FRAME, LIKE EVERY OTHER PART OF THIS FORMAT: each
    /// call compresses/decompresses exactly one frame's own plane, in
    /// isolation, with no reference to any other frame's bytes. This is
    /// load-bearing, not incidental — see ScrubProxyFormat's own class
    /// remarks on why the whole point of this file format is O(1) random-
    /// access reads with no keyframe/GOP concept; a cross-frame delta
    /// scheme (Zstd dictionaries built from a neighboring frame, a
    /// streaming context carried across frames, etc.) would reintroduce
    /// exactly the keyframe-seeking problem this subsystem exists to
    /// eliminate, and must never be pursued here regardless of any future
    /// size-reduction opportunity that might come from it.
    /// </summary>
    internal static class ScrubProxyZstd
    {
        /// <summary>
        /// The one-time build-time compression level Encode uses. 19 is a
        /// deliberately aggressive choice (zstd's own "high compression"
        /// range starts around here; ZstdSharp.Port's own maximum, exposed
        /// as Compressor's ZSTD_maxCLevel()-derived ceiling, typically goes
        /// up to 22) — justified specifically by this scheme's own reason
        /// for existing (see class remarks): decode speed doesn't pay for
        /// a higher encode level, and encoding only ever happens once, at
        /// build time, off the per-tick scrub path entirely (see
        /// ScrubProxyCache.BuildAsync). NOT the library's absolute max
        /// (22) — the last few levels buy diminishing compression for a
        /// real, sometimes large, additional one-time build-time cost, and
        /// 19 was picked as a reasonable starting trade-off rather than a
        /// value tuned against real proxy content.
        ///
        /// FLAGGED AS AN UNTUNED STARTING POINT, same as IndexedDelta7Codec's
        /// own RStep/GStep/BStep constants — worth real-hardware
        /// measurement (build time vs. resulting file size, across a range
        /// of levels) before treating 19 as anything more than a
        /// reasonable first guess.
        /// </summary>
        public const int CompressionLevel = 19;

        /// <summary>
        /// Encodes `raw` into a fresh, exactly-sized buffer using zstd at
        /// CompressionLevel. The caller decides what to do with the result.
        /// </summary>
        public static byte[] Encode(ReadOnlySpan<byte> raw)
        {
            using var compressor = new Compressor(CompressionLevel);
            return compressor.Wrap(raw).ToArray();
        }

        /// <summary>
        /// Decodes `compressed` into `destination`, which must be exactly
        /// the known decompressed size: ScrubProxyReader always knows the
        /// exact expected size ahead of time (Width * Height * 4 for an
        /// Rgba8888 frame, or Width * Height for an IndexedDelta7 frame's
        /// own control-byte plane), so there's no need to let Unwrap
        /// allocate or guess a size — writing directly into a caller-
        /// supplied buffer avoids an extra copy on every single scrub
        /// tick's decode. Throws if `compressed` decodes to a different
        /// number of bytes than `destination`'s length, which means a
        /// corrupt/truncated file or a version/scheme mismatch that
        /// slipped past ReadHeader, not a normal runtime condition.
        /// </summary>
        public static void Decode(ReadOnlySpan<byte> compressed, Span<byte> destination)
        {
            using var decompressor = new Decompressor();
            int written = decompressor.Unwrap(compressed, destination);

            if (written != destination.Length)
                throw new InvalidOperationException(
                    $"ScrubProxyZstd.Decode: decoded {written} bytes, expected exactly " +
                    $"{destination.Length} — the compressed frame data is corrupt, truncated, or was " +
                    "encoded for a different frame size.");
        }
    }
}