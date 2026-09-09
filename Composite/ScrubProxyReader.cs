using System;
using System.Buffers;
using System.IO;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using SkiaSharp;

namespace EditSharp.Composite
{
    /// <summary>
    /// Reads one .esrp scrub-proxy file (see ScrubProxyFormat) — no ffmpeg,
    /// no decode process, no child process at all. GetFrameAt is a frame-
    /// index lookup (which byte range a timestamp's nearest stored frame
    /// occupies) plus one direct positioned read via System.IO.RandomAccess
    /// (and, depending on the file's own PixelFormat/CompressionScheme, one
    /// fast in-memory decode of that read — a Zstd decompression pass, a
    /// palette+delta expansion, or both — see GetFrameAt below) — this is
    /// what lets GetFrameAt be genuinely safe to call back-to-back as fast
    /// as a caller likes, with no per-call process, no per-call I/O wait
    /// beyond one small positioned read, and (unlike a shared FileStream's
    /// Seek+Read) no shared cursor state a second concurrent caller could
    /// race against.
    ///
    /// THE FRAME INDEX TABLE — AND, FOR AN IndexedDelta7 FILE, ITS ONE
    /// SHARED PALETTE — ARE READ ONCE, IN FULL, AT Open() TIME, AND KEPT IN
    /// MEMORY for this reader's whole life. The table itself is small
    /// (FrameCount * 12 bytes) even for a long/high-sample-rate source, and
    /// the shared palette is smaller still (384 bytes) — both cost one
    /// extra small positioned read per SOURCE, not per tick, in exchange
    /// for GetFrameAt never touching either section's own on-disk bytes
    /// again.
    ///
    /// ONE READER PER SOURCE, OWNED BY ITS ScrubFrameSource/caller — not a
    /// process-wide singleton. The shared mutable scratch fields
    /// (_frameBuffer, and — for an IndexedDelta7 file only —
    /// _indexScratchBuffer) are reused across calls to avoid a fresh
    /// allocation every scrub tick; see ScrubFrameSource's own remarks on
    /// why multi-clip prefetch is still sequential, not parallel, in this
    /// pass — that's what keeps reusing these buffers safe without a lock.
    /// A frame read off a COMPRESSED and/or IndexedDelta7 file additionally
    /// rents a scratch buffer for the raw on-disk bytes themselves
    /// (ArrayPool&lt;byte&gt;.Shared, sized to exactly this frame's own
    /// recorded length, returned before the call returns) — the decode
    /// target is always `_frameBuffer` (always full RGBA8888), so callers
    /// see the exact same "fresh SKImage over a stable-sized raw buffer"
    /// shape regardless of PixelFormat/CompressionScheme.
    ///
    /// DECOMPRESSION DISPATCH IS SHARED — DecompressPlane below is the ONE
    /// place that switches on CompressionScheme (None/Zstd) to turn a
    /// frame's stored bytes back into a plane; ReadRgba8888Frame/
    /// ReadIndexedDelta7Frame both route through it once they have that
    /// frame's own compressed bytes in hand. ReadRgba8888Frame keeps its
    /// own direct-read fast path for the None case specifically (reading
    /// straight into `_frameBuffer` with no intermediate rented buffer at
    /// all).
    ///
    /// SHARED/GLOBAL PALETTE FOR IndexedDelta7 (V6, see ScrubProxyFormat's
    /// VERSION 6 remarks and IndexedDelta7Codec's own SHARED/GLOBAL PALETTE
    /// (V6) remarks): an IndexedDelta7 file's ONE palette (`_globalPalette`
    /// below) is read once here, at Open() time, from the file's own
    /// [GlobalPalette] section (see ScrubProxyFormat.GlobalPaletteOffset) —
    /// exactly like the frame index table already is. ReadIndexedDelta7Frame
    /// doesn't split a per-frame palette off the front of each frame's own
    /// blob at all — a frame's WHOLE recorded blob is just its
    /// compressed-or-not control-byte plane, decompressed directly via
    /// DecompressPlane into `_indexScratchBuffer`, then expanded against
    /// `_globalPalette` via IndexedDelta7Codec.Decode. `_globalPalette` is
    /// null for an Rgba8888 file (GlobalPaletteLength is always 0 for
    /// those).
    /// </summary>
    internal sealed class ScrubProxyReader : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly byte[] _frameBuffer;
        private readonly int _frameByteSize;
        private readonly int _pixelCount;

        /// <summary>
        /// Only allocated/used when PixelFormat is IndexedDelta7 — holds
        /// one frame's decompressed (or raw, if CompressionScheme.None)
        /// control bytes before IndexedDelta7Codec.Decode turns them into
        /// _frameBuffer's full RGBA8888. Reused across calls exactly like
        /// _frameBuffer.
        /// </summary>
        private readonly byte[]? _indexScratchBuffer;

        /// <summary>
        /// See class remarks, SHARED/GLOBAL PALETTE FOR IndexedDelta7 (V6).
        /// Read once, in full, at Open() time from the file's own
        /// [GlobalPalette] section; null for an Rgba8888 file.
        /// </summary>
        private readonly byte[]? _globalPalette;

        private readonly (long Offset, int Length)[] _frameIndex;
        private readonly ScrubProxyPixelFormat _pixelFormat;
        private readonly ScrubProxyCompressionScheme _compressionScheme;

        public string Path { get; }
        public int Width { get; }
        public int Height { get; }
        public double SampleRate { get; }
        public int FrameCount { get; }

        /// <summary>
        /// The embedded metadata blob, parsed once at Open() time — see
        /// ScrubProxyMeta. A caller that only needs Width/Height/
        /// SampleRate/FrameCount (the common case) never needs to touch
        /// this at all.
        /// </summary>
        public ScrubProxyMeta Meta { get; }

        private ScrubProxyReader(
            SafeFileHandle handle, string path, ScrubProxyFormat.Header header,
            (long Offset, int Length)[] frameIndex, byte[]? globalPalette, ScrubProxyMeta meta)
        {
            _handle = handle;
            Path = path;
            Width = header.Width;
            Height = header.Height;
            SampleRate = header.SampleRate;
            FrameCount = header.FrameCount;
            _pixelFormat = header.PixelFormat;
            _compressionScheme = header.CompressionScheme;
            _pixelCount = header.Width * header.Height;
            _frameByteSize = _pixelCount * 4;
            _frameBuffer = new byte[_frameByteSize];
            _indexScratchBuffer =
                _pixelFormat == ScrubProxyPixelFormat.IndexedDelta7
                    ? new byte[_pixelCount]
                    : null;
            _globalPalette = globalPalette;
            _frameIndex = frameIndex;
            Meta = meta;
        }

        /// <summary>
        /// Opens `path`, reads its fixed header, embedded metadata blob,
        /// shared-palette section (if any — see class remarks), and full
        /// frame index table, and returns a reader ready for GetFrameAt
        /// calls. The handle stays open for this reader's whole life — see
        /// class remarks.
        /// </summary>
        public static ScrubProxyReader Open(string path)
        {
            // FileOptions.RandomAccess is a hint to the OS cache/readahead
            // strategy (this file is never read sequentially in the
            // GetFrameAt hot path), not a correctness requirement —
            // RandomAccess.Read below works regardless, but the hint is
            // free and matches actual usage.
            SafeFileHandle handle = File.OpenHandle(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);

            try
            {
                (ScrubProxyFormat.Header header, ScrubProxyMeta meta) = ReadHeaderAndMetaFromHandle(handle, path);

                byte[]? globalPalette = null;
                if (header.GlobalPaletteLength > 0)
                {
                    long globalPaletteOffset = ScrubProxyFormat.GlobalPaletteOffset(header.MetaBlobLength);
                    globalPalette = new byte[header.GlobalPaletteLength];
                    if (ReadFullyAt(handle, globalPalette, globalPaletteOffset) != globalPalette.Length)
                        throw new InvalidDataException($"'{path}' is truncated — could not read its shared palette.");
                }

                long frameIndexOffset =
                    ScrubProxyFormat.FrameIndexOffset(header.MetaBlobLength, header.GlobalPaletteLength);
                int frameIndexByteSize = header.FrameCount * ScrubProxyFormat.FrameIndexEntrySize;
                byte[] frameIndexBytes = new byte[frameIndexByteSize];
                if (ReadFullyAt(handle, frameIndexBytes, frameIndexOffset) != frameIndexBytes.Length)
                    throw new InvalidDataException($"'{path}' is truncated — could not read its frame index.");

                var frameIndex = new (long Offset, int Length)[header.FrameCount];
                for (int i = 0; i < header.FrameCount; i++)
                {
                    frameIndex[i] = ScrubProxyFormat.ReadFrameIndexEntry(
                        frameIndexBytes.AsSpan(
                            i * ScrubProxyFormat.FrameIndexEntrySize, ScrubProxyFormat.FrameIndexEntrySize));
                }

                return new ScrubProxyReader(handle, path, header, frameIndex, globalPalette, meta);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Reads just the fixed header and embedded metadata blob of
        /// `path` — NOT the shared-palette section or the frame index, and
        /// doesn't keep the file open — for a caller (ScrubProxyCache.
        /// TryLoadExisting) that only needs to validate/describe an entry,
        /// not actually read frames from it. Cheaper than a full Open() for
        /// that purpose alone.
        /// </summary>
        public static (ScrubProxyFormat.Header Header, ScrubProxyMeta Meta) ReadHeaderAndMeta(string path)
        {
            using SafeFileHandle handle = File.OpenHandle(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);

            return ReadHeaderAndMetaFromHandle(handle, path);
        }

        private static (ScrubProxyFormat.Header Header, ScrubProxyMeta Meta) ReadHeaderAndMetaFromHandle(
            SafeFileHandle handle, string path)
        {
            byte[] headerBytes = new byte[ScrubProxyFormat.HeaderSize];
            if (ReadFullyAt(handle, headerBytes, 0) != headerBytes.Length)
                throw new InvalidDataException($"'{path}' is truncated — could not read its scrub-proxy header.");

            ScrubProxyFormat.Header header = ScrubProxyFormat.ReadHeader(headerBytes, path);

            byte[] metaBytes = header.MetaBlobLength > 0 ? new byte[header.MetaBlobLength] : Array.Empty<byte>();
            if (metaBytes.Length > 0 &&
                ReadFullyAt(handle, metaBytes, ScrubProxyFormat.MetaBlobOffset) != metaBytes.Length)
            {
                throw new InvalidDataException($"'{path}' is truncated — could not read its embedded metadata blob.");
            }

            ScrubProxyMeta meta = ScrubProxyMetaSerializer.Deserialize(metaBytes, path);
            return (header, meta);
        }

        private static int ReadFullyAt(SafeFileHandle handle, Span<byte> buffer, long offset)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = RandomAccess.Read(handle, buffer.Slice(total), offset + total);
                if (read == 0) break; // real EOF — shouldn't happen against a well-formed file, tolerated not assumed
                total += read;
            }
            return total;
        }

        /// <summary>
        /// The stored frame nearest `seconds` — floor(seconds * SampleRate),
        /// clamped to the last stored frame past the proxy's own recorded
        /// end (a clip-relative time that runs past a freeze-framed source's
        /// real content lands on that source's own last decoded frame,
        /// matching every other freeze-frame-on-exhaustion behavior in this
        /// codebase — see SkSourceDecoder.NextFrame's own remarks).
        ///
        /// Returns a fresh SKImage owning its OWN copy of the pixel data
        /// (via SKData.CreateCopy) — safe to keep/dispose independently of
        /// this reader's internal reusable buffer, which the NEXT call to
        /// this method overwrites. ALWAYS returns full RGBA8888 pixels
        /// regardless of this file's own on-disk PixelFormat.
        /// </summary>
        public SKImage GetFrameAt(double seconds)
        {
            int index = (int)Math.Floor(seconds * SampleRate);
            index = Math.Clamp(index, 0, FrameCount - 1);

            (long offset, int length) = _frameIndex[index];

            switch (_pixelFormat)
            {
                case ScrubProxyPixelFormat.IndexedDelta7:
                    ReadIndexedDelta7Frame(offset, length, index);
                    break;
                default:
                    ReadRgba8888Frame(offset, length, index);
                    break;
            }

            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            SKData data = SKData.CreateCopy(_frameBuffer);
            return SKImage.FromPixels(info, data, Width * 4);
        }

        /// <summary>
        /// Rgba8888 path: either a direct read straight into `_frameBuffer`
        /// (None — no rented scratch buffer needed at all), or a rented-
        /// scratch read followed by DecompressPlane into `_frameBuffer`
        /// (Zstd).
        /// </summary>
        private void ReadRgba8888Frame(long offset, int length, int frameIndex)
        {
            if (_compressionScheme == ScrubProxyCompressionScheme.None)
            {
                int totalRead = ReadFullyAt(_handle, _frameBuffer, offset);
                if (totalRead != _frameByteSize)
                    throw new InvalidOperationException(
                        $"ScrubProxyReader('{Path}') read {totalRead}/{_frameByteSize} raw bytes for frame " +
                        $"{frameIndex} at offset {offset} — the file may be truncated or corrupt.");
                return;
            }

            byte[] compressed = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                int totalRead = ReadFullyAt(_handle, compressed.AsSpan(0, length), offset);
                if (totalRead != length)
                    throw new InvalidOperationException(
                        $"ScrubProxyReader('{Path}') read {totalRead}/{length} compressed bytes for frame " +
                        $"{frameIndex} at offset {offset} — the file may be truncated or corrupt.");

                DecompressPlane(compressed.AsSpan(0, length), _frameBuffer, frameIndex, "Rgba8888");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(compressed);
            }
        }

        /// <summary>
        /// IndexedDelta7 path — this frame's whole `length`-byte blob is
        /// JUST its control-byte plane — no embedded palette to split off
        /// (that lives in `_globalPalette`, read once at Open() time).
        /// Reads the whole blob into a rented scratch buffer, decompresses
        /// it directly into `_indexScratchBuffer` via DecompressPlane, and
        /// expands against `_globalPalette` via IndexedDelta7Codec.Decode.
        /// </summary>
        private void ReadIndexedDelta7Frame(long offset, int length, int frameIndex)
        {
            if (_globalPalette == null)
                throw new InvalidOperationException(
                    $"ScrubProxyReader('{Path}') is an IndexedDelta7 file with no shared palette recorded — " +
                    "the file is corrupt or was written by an incompatible build.");

            byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
            try
            {
                int totalRead = ReadFullyAt(_handle, rented.AsSpan(0, length), offset);
                if (totalRead != length)
                    throw new InvalidOperationException(
                        $"ScrubProxyReader('{Path}') read {totalRead}/{length} bytes for IndexedDelta7 frame " +
                        $"{frameIndex} at offset {offset} — the file may be truncated or corrupt.");

                ReadOnlySpan<byte> pixelCodes = rented.AsSpan(0, length);

                DecompressPlane(pixelCodes, _indexScratchBuffer!, frameIndex, "IndexedDelta7");

                IndexedDelta7Codec.Decode(_globalPalette, _indexScratchBuffer!, Width, Height, _frameBuffer);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        /// <summary>
        /// SHARED None/Zstd DISPATCH. Turns `compressed` (this frame's own
        /// stored plane bytes — never a palette, and never more than one
        /// frame's worth) into `plane` (an already-correctly-sized
        /// destination — either `_frameBuffer` directly for the Rgba8888
        /// case, or `_indexScratchBuffer` for IndexedDelta7), dispatching
        /// on `_compressionScheme`. `frameKind` is purely for a clearer
        /// exception message.
        /// </summary>
        private void DecompressPlane(ReadOnlySpan<byte> compressed, Span<byte> plane, int frameIndex, string frameKind)
        {
            switch (_compressionScheme)
            {
                case ScrubProxyCompressionScheme.None:
                    if (compressed.Length != plane.Length)
                        throw new InvalidOperationException(
                            $"ScrubProxyReader('{Path}') found {compressed.Length} raw {frameKind} plane bytes " +
                            $"for frame {frameIndex}, expected exactly {plane.Length} — the file may be " +
                            "truncated or corrupt.");
                    compressed.CopyTo(plane);
                    break;

                case ScrubProxyCompressionScheme.Zstd:
                    ScrubProxyZstd.Decode(compressed, plane);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"ScrubProxyReader('{Path}') encountered an unknown compression scheme " +
                        $"'{_compressionScheme}' while reading frame {frameIndex}'s {frameKind} plane.");
            }
        }

        public void Dispose() => _handle.Dispose();
    }
}