using System;
using System.Buffers;
using System.IO;
using Microsoft.Win32.SafeHandles;
using SkiaSharp;

namespace EditSharp.Caching.Proxy
{
    /// <summary>Whether a proxy can hand back a given frame right now.</summary>
    internal enum ProxyFrameAvailability
    {
        Ready,

        /// <summary>The proxy is still being built (or was interrupted) and hasn't reached this frame.</summary>
        Pending,

        /// <summary>The proxy is complete and this frame is past its end.</summary>
        PastEnd,
    }

    /// <summary>
    /// Reads one .esrp proxy (see EsrpFormat) with no subprocess and no
    /// decoder: a frame is one positioned read plus, depending on the file's
    /// format, a Zstd decompression and/or a palette expansion.
    ///
    /// SAFE AGAINST A FILE STILL BEING WRITTEN: the file is opened sharing
    /// write access, the index is cached only as far as it's known to be
    /// filled, and asking for a frame beyond that re-reads just the missing
    /// stretch of the index (and the header's Complete flag) from disk. A
    /// growing proxy therefore becomes readable frame by frame without ever
    /// reopening the reader.
    ///
    /// One reader per consumer: the scratch buffers are reused across calls
    /// and are not synchronized.
    /// </summary>
    internal sealed class EsrpReader : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly EsrpFormat.Header _header;
        private readonly (long Offset, int Length)[] _index;
        private readonly byte[]? _palette;
        private readonly byte[] _frameBuffer;
        private readonly byte[]? _codeBuffer;

        //frames [0, _filled) are known to be written; beyond that the index is re-read on demand
        private int _filled;
        private bool _complete;
        private int _frameCount;

        public string Path { get; }
        public int Width => _header.Width;
        public int Height => _header.Height;
        public double FrameRate => _header.FrameRate;
        public EsrpPixelFormat PixelFormat => _header.PixelFormat;
        public EsrpMeta Meta { get; }

        /// <summary>How many frames, from the start, are readable as of the last refresh.</summary>
        public int AvailableFrames => _filled;

        public bool IsComplete => _complete;

        private EsrpReader(SafeFileHandle handle, string path, EsrpFormat.Header header, EsrpMeta meta, byte[]? palette)
        {
            _handle = handle;
            Path = path;
            _header = header;
            Meta = meta;
            _palette = palette;
            _index = new (long, int)[header.Capacity];
            _frameBuffer = new byte[header.Width * header.Height * 4];
            _codeBuffer = header.PixelFormat == EsrpPixelFormat.IndexedDelta7 ? new byte[header.Width * header.Height] : null;
            _complete = header.Complete;
            _frameCount = header.FrameCount;
        }

        public static EsrpReader Open(string path)
        {
            SafeFileHandle handle = File.OpenHandle(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);

            try
            {
                (EsrpFormat.Header header, EsrpMeta meta) = ReadHeaderAndMeta(handle, path);

                byte[]? palette = null;
                if (header.PaletteLength > 0)
                {
                    palette = new byte[header.PaletteLength];
                    ReadExactly(handle, palette, EsrpFormat.PaletteOffset(header.MetaLength), path, "shared palette");
                }

                var reader = new EsrpReader(handle, path, header, meta, palette);
                reader.Refresh(header.Capacity - 1);
                return reader;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public static (EsrpFormat.Header Header, EsrpMeta Meta) ReadHeaderAndMeta(string path)
        {
            using SafeFileHandle handle = File.OpenHandle(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
            return ReadHeaderAndMeta(handle, path);
        }

        private static (EsrpFormat.Header, EsrpMeta) ReadHeaderAndMeta(SafeFileHandle handle, string path)
        {
            byte[] headerBytes = new byte[EsrpFormat.HeaderSize];
            ReadExactly(handle, headerBytes, 0, path, "header");
            EsrpFormat.Header header = EsrpFormat.ReadHeader(headerBytes, path);

            byte[] metaBytes = new byte[header.MetaLength];
            ReadExactly(handle, metaBytes, EsrpFormat.MetaOffset, path, "meta blob");

            return (header, EsrpMetaSerializer.Deserialize(metaBytes, path));
        }

        /// <summary>The frame covering `seconds`, if it's been written.</summary>
        public ProxyFrameAvailability TryGetFrameAt(double seconds, out SKImage? image) =>
            TryGetFrame((int)Math.Floor(Math.Max(0, seconds) * FrameRate), out image);

        /// <summary>
        /// Frame `frame` as a fresh image the caller owns, or why not. Past the
        /// preallocated capacity is PastEnd even while building; the build
        /// never writes beyond it.
        /// </summary>
        public ProxyFrameAvailability TryGetFrame(int frame, out SKImage? image)
        {
            image = null;

            if (frame >= _header.Capacity) return ProxyFrameAvailability.PastEnd;
            if (frame >= _filled && !_complete) Refresh(frame);
            if (_complete && frame >= _frameCount) return ProxyFrameAvailability.PastEnd;
            if (frame >= _filled) return ProxyFrameAvailability.Pending;

            (long offset, int length) = _index[frame];

            if (_header.PixelFormat == EsrpPixelFormat.IndexedDelta7)
                ReadIndexedDelta7(offset, length, frame);
            else
                ReadRgba8888(offset, length, frame);

            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            image = SKImage.FromPixels(info, SKData.CreateCopy(_frameBuffer), Width * 4);
            return ProxyFrameAvailability.Ready;
        }

        /// <summary>
        /// Re-reads the header's completion fields and the index from the first
        /// unknown entry up to `upTo`, advancing the known-filled prefix. The
        /// flags are read FIRST: a writer fills every entry before setting
        /// Complete, so seeing Complete guarantees the entries read after it
        /// are final.
        /// </summary>
        private void Refresh(int upTo)
        {
            if (!_complete)
            {
                byte[] tail = new byte[8];
                ReadExactly(_handle, tail, EsrpFormat.FlagsOffset, Path, "header");
                _complete = (BitConverter.ToInt32(tail.AsSpan(0, 4)) & EsrpFormat.CompleteFlag) != 0;
                _frameCount = BitConverter.ToInt32(tail.AsSpan(4, 4));
            }

            int end = Math.Min(upTo + 1, _header.Capacity);
            if (_filled >= end) return;

            byte[] bytes = new byte[(end - _filled) * EsrpFormat.FrameIndexEntrySize];
            long indexOffset = EsrpFormat.IndexOffset(_header.MetaLength, _header.PaletteLength);
            ReadExactly(_handle, bytes, indexOffset + (long)_filled * EsrpFormat.FrameIndexEntrySize, Path, "frame index");

            for (int i = _filled; i < end; i++)
            {
                (long offset, int length) entry = EsrpFormat.ReadIndexEntry(
                    bytes.AsSpan((i - _filled) * EsrpFormat.FrameIndexEntrySize, EsrpFormat.FrameIndexEntrySize));

                if (entry.length == 0)
                {
                    end = i;
                    break;
                }

                _index[i] = entry;
            }

            _filled = end;
        }

        private void ReadRgba8888(long offset, int length, int frame)
        {
            if (_header.CompressionScheme == EsrpCompressionScheme.None)
            {
                ReadExactly(_handle, _frameBuffer, offset, Path, $"frame {frame}");
                return;
            }

            byte[] stored = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                ReadExactly(_handle, stored.AsSpan(0, length), offset, Path, $"frame {frame}");
                Decompress(stored.AsSpan(0, length), _frameBuffer, frame);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(stored);
            }
        }

        private void ReadIndexedDelta7(long offset, int length, int frame)
        {
            if (_palette is null)
                throw new InvalidDataException($"'{Path}' is IndexedDelta7 but has no shared palette.");

            byte[] stored = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                ReadExactly(_handle, stored.AsSpan(0, length), offset, Path, $"frame {frame}");
                Decompress(stored.AsSpan(0, length), _codeBuffer!, frame);
                IndexedDelta7Codec.Decode(_palette, _codeBuffer!, Width, Height, _frameBuffer);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(stored);
            }
        }

        private void Decompress(ReadOnlySpan<byte> stored, Span<byte> plane, int frame)
        {
            switch (_header.CompressionScheme)
            {
                case EsrpCompressionScheme.None:
                    if (stored.Length != plane.Length)
                        throw new InvalidDataException($"'{Path}' frame {frame} is {stored.Length} bytes, expected {plane.Length}.");
                    stored.CopyTo(plane);
                    break;

                case EsrpCompressionScheme.Zstd:
                    EsrpZstd.Decode(stored, plane);
                    break;
            }
        }

        private static void ReadExactly(SafeFileHandle handle, Span<byte> buffer, long offset, string path, string what)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = RandomAccess.Read(handle, buffer[total..], offset + total);
                if (read == 0)
                    throw new InvalidDataException($"'{path}' is truncated; could not read its {what}.");
                total += read;
            }
        }

        public void Dispose() => _handle.Dispose();
    }
}
