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
    /// fast in-memory decode of that read — an RLE decode, a palette
    /// expansion, or both — see GetFrameAt below) — this is what lets
    /// GetFrameAt be genuinely safe to call back-to-back as fast as a
    /// caller likes, with no per-call process, no per-call I/O wait beyond
    /// one small positioned read, and (unlike a shared FileStream's
    /// Seek+Read) no shared cursor state a second concurrent caller could
    /// race against.
    ///
    /// THE FRAME INDEX TABLE IS READ ONCE, IN FULL, AT Open() TIME, AND
    /// KEPT IN MEMORY for this reader's whole life — see ScrubProxyFormat's
    /// VERSION 2 remarks for why v2 needs a table at all (compressed/
    /// Indexed8 frames are variable-size, so GetFrameAt can no longer
    /// compute an offset by plain multiplication). The table itself is
    /// small (FrameCount * 12 bytes) even for a long/high-sample-rate
    /// source, so reading all of it up front costs one extra small
    /// positioned read per SOURCE, not per tick, in exchange for
    /// GetFrameAt never touching the table's own on-disk bytes again.
    ///
    /// ONE READER PER SOURCE, OWNED BY ITS ScrubFrameSource/caller — not a
    /// process-wide singleton. The shared mutable scratch fields
    /// (_frameBuffer, and — for Indexed8 files only — _indexScratchBuffer)
    /// are reused across calls to avoid a fresh allocation every scrub
    /// tick; see ScrubFrameSource's own remarks on why multi-clip prefetch
    /// is still sequential, not parallel, in this pass — that's what keeps
    /// reusing these buffers safe without a lock. A frame read off a
    /// COMPRESSED and/or Indexed8 file additionally rents a scratch buffer
    /// for the raw on-disk bytes themselves (ArrayPool&lt;byte&gt;.Shared,
    /// sized to exactly this frame's own recorded length, returned before
    /// the call returns) — the decode target is always `_frameBuffer`
    /// (always full RGBA8888), so callers see the exact same "fresh
    /// SKImage over a stable-sized raw buffer" shape regardless of
    /// PixelFormat/CompressionScheme.
    /// </summary>
    internal sealed class ScrubProxyReader : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly byte[] _frameBuffer;
        private readonly int _frameByteSize;
        private readonly int _pixelCount;

        /// <summary>
        /// Only allocated/used when PixelFormat is Indexed8 — holds one
        /// frame's decompressed (or raw, if CompressionScheme.None) index
        /// bytes before Expand() turns them into _frameBuffer's full
        /// RGBA8888. Reused across calls exactly like _frameBuffer.
        /// </summary>
        private readonly byte[]? _indexScratchBuffer;

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
            (long Offset, int Length)[] frameIndex, ScrubProxyMeta meta)
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
            _indexScratchBuffer = _pixelFormat == ScrubProxyPixelFormat.Indexed8 ? new byte[_pixelCount] : null;
            _frameIndex = frameIndex;
            Meta = meta;
        }

        /// <summary>
        /// Opens `path`, reads its fixed header, embedded metadata blob,
        /// and full frame index table, and returns a reader ready for
        /// GetFrameAt calls. The handle stays open for this reader's whole
        /// life — see class remarks.
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

                long frameIndexOffset = ScrubProxyFormat.FrameIndexOffset(header.MetaBlobLength);
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

                return new ScrubProxyReader(handle, path, header, frameIndex, meta);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Reads just the fixed header and embedded metadata blob of
        /// `path` — NOT the frame index, and doesn't keep the file open —
        /// for a caller (ScrubProxyCache.TryLoadExistingAsync) that only
        /// needs to validate/describe an entry, not actually read frames
        /// from it. Cheaper than a full Open() for that purpose alone.
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
        /// regardless of this file's own on-disk PixelFormat — an
        /// Indexed8 file's palette+index blob is expanded into
        /// `_frameBuffer` via ColorQuantizer.Expand before the SKImage is
        /// built, so ScrubFrameSource/Playback need zero awareness of
        /// PixelFormat at all.
        /// </summary>
        public SKImage GetFrameAt(double seconds)
        {
            int index = (int)Math.Floor(seconds * SampleRate);
            index = Math.Clamp(index, 0, FrameCount - 1);

            (long offset, int length) = _frameIndex[index];

            if (_pixelFormat == ScrubProxyPixelFormat.Indexed8)
                ReadIndexedFrame(offset, length, index);
            else
                ReadRgba8888Frame(offset, length, index);

            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            SKData data = SKData.CreateCopy(_frameBuffer);
            return SKImage.FromPixels(info, data, Width * 4);
        }

        /// <summary>
        /// Rgba8888 path — unchanged from v2's own GetFrameAt logic:
        /// either a direct read straight into `_frameBuffer` (None), or a
        /// rented-scratch read followed by an in-place Rle decode into
        /// `_frameBuffer` (Rle).
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

            // Only None/Rle are valid CompressionScheme values — ReadHeader
            // already rejects anything else — so this is deliberately a
            // plain else, not a switch. A future third scheme would need
            // this changed to a real switch.
            byte[] compressed = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                int totalRead = ReadFullyAt(_handle, compressed.AsSpan(0, length), offset);
                if (totalRead != length)
                    throw new InvalidOperationException(
                        $"ScrubProxyReader('{Path}') read {totalRead}/{length} compressed bytes for frame " +
                        $"{frameIndex} at offset {offset} — the file may be truncated or corrupt.");

                ScrubProxyRle.Decode(compressed.AsSpan(0, length), _frameBuffer);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(compressed);
            }
        }

        /// <summary>
        /// Indexed8 path — see ScrubProxyPixelFormat.Indexed8's own remarks
        /// for the on-disk shape: this frame's whole `length`-byte blob is
        /// [256*4-byte raw palette][index bytes, optionally Rle'd]. Reads
        /// the WHOLE blob into a rented scratch buffer (unlike Rgba8888's
        /// None case, which can read straight into `_frameBuffer` — here
        /// the palette has to be split off first regardless of
        /// CompressionScheme, so there's no equivalent direct-read
        /// shortcut), splits it at the fixed 1024-byte palette boundary,
        /// decodes the index bytes (Rle or a straight copy) into
        /// `_indexScratchBuffer`, and finally expands palette+indices into
        /// `_frameBuffer` via ColorQuantizer.Expand.
        /// </summary>
        private void ReadIndexedFrame(long offset, int length, int frameIndex)
        {
            const int paletteSize = ScrubProxyFormat.IndexedPaletteByteSize;

            if (length < paletteSize)
                throw new InvalidOperationException(
                    $"ScrubProxyReader('{Path}') found a frame {frameIndex} blob of only {length} bytes — " +
                    $"too short to contain even an Indexed8 palette ({paletteSize} bytes). The file may be " +
                    "truncated or corrupt.");

            byte[] rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                int totalRead = ReadFullyAt(_handle, rented.AsSpan(0, length), offset);
                if (totalRead != length)
                    throw new InvalidOperationException(
                        $"ScrubProxyReader('{Path}') read {totalRead}/{length} bytes for Indexed8 frame " +
                        $"{frameIndex} at offset {offset} — the file may be truncated or corrupt.");

                ReadOnlySpan<byte> palette = rented.AsSpan(0, paletteSize);
                ReadOnlySpan<byte> indexBytes = rented.AsSpan(paletteSize, length - paletteSize);

                if (_compressionScheme == ScrubProxyCompressionScheme.None)
                {
                    if (indexBytes.Length != _pixelCount)
                        throw new InvalidOperationException(
                            $"ScrubProxyReader('{Path}') found {indexBytes.Length} raw index bytes for frame " +
                            $"{frameIndex}, expected exactly {_pixelCount} — the file may be truncated or corrupt.");
                    indexBytes.CopyTo(_indexScratchBuffer!);
                }
                else
                {
                    ScrubProxyRle.Decode(indexBytes, _indexScratchBuffer!);
                }

                ColorQuantizer.Expand(palette, _indexScratchBuffer!, _frameBuffer);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public void Dispose() => _handle.Dispose();
    }
}