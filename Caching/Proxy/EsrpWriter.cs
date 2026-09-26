using System;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace EditSharp.Caching.Proxy
{
    /// <summary>
    /// Writes an .esrp proxy progressively (see EsrpFormat): every Append writes the frame's data, then its index entry,
    /// each as one positioned write straight to the OS, so a concurrent
    /// EsrpReader never sees an entry before its data. Complete stamps the
    /// actual frame count and the Complete flag last.
    ///
    /// Resume reopens an interrupted file, keeps its contiguous written
    /// prefix, trims anything after it (a frame whose data landed but whose
    /// index entry didn't), and carries on appending from there.
    /// </summary>
    internal sealed class EsrpWriter : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly long _indexOffset;
        private long _cursor;

        public string Path { get; }
        public EsrpFormat.Header Header { get; private set; }
        public EsrpMeta Meta { get; }

        /// <summary>The shared IndexedDelta7 palette (empty for Rgba8888).</summary>
        public byte[] Palette { get; }

        public int FramesWritten { get; private set; }

        private EsrpWriter(SafeFileHandle handle, string path, EsrpFormat.Header header, EsrpMeta meta, byte[] palette, int framesWritten, long cursor)
        {
            _handle = handle;
            Path = path;
            Header = header;
            Meta = meta;
            Palette = palette;
            FramesWritten = framesWritten;
            _cursor = cursor;
            _indexOffset = EsrpFormat.IndexOffset(header.MetaLength, header.PaletteLength);
        }

        public static EsrpWriter Create(
            string path, int width, int height, EsrpPixelFormat pixelFormat, EsrpCompressionScheme compression,
            Rational frameRate, int capacity, EsrpMeta meta, byte[] palette)
        {
            byte[] metaBytes = EsrpMetaSerializer.SerializeToUtf8Bytes(meta);

            var header = new EsrpFormat.Header(
                width, height, pixelFormat, compression, frameRate, capacity, metaBytes.Length, palette.Length,
                Complete: false, FrameCount: 0);

            SafeFileHandle handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);

            try
            {
                byte[] headerBytes = new byte[EsrpFormat.HeaderSize];
                EsrpFormat.WriteHeader(headerBytes, header);

                RandomAccess.Write(handle, headerBytes, 0);
                RandomAccess.Write(handle, metaBytes, EsrpFormat.MetaOffset);
                if (palette.Length > 0) RandomAccess.Write(handle, palette, EsrpFormat.PaletteOffset(metaBytes.Length));

                //the zeroed index is what tells readers "not written yet"
                long indexOffset = EsrpFormat.IndexOffset(metaBytes.Length, palette.Length);
                RandomAccess.Write(handle, new byte[(long)capacity * EsrpFormat.FrameIndexEntrySize], indexOffset);

                long dataStart = EsrpFormat.DataStartOffset(metaBytes.Length, palette.Length, capacity);
                return new EsrpWriter(handle, path, header, meta, palette, 0, dataStart);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public static EsrpWriter Resume(string path)
        {
            SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);

            try
            {
                using EsrpReader reader = EsrpReader.Open(path);

                if (reader.IsComplete)
                    throw new InvalidOperationException($"'{path}' is already complete; there's nothing to resume.");

                (EsrpFormat.Header header, EsrpMeta meta) = EsrpReader.ReadHeaderAndMeta(path);

                byte[] palette = new byte[header.PaletteLength];
                if (palette.Length > 0) ReadExactly(handle, palette, EsrpFormat.PaletteOffset(header.MetaLength));

                int written = reader.AvailableFrames;
                long indexOffset = EsrpFormat.IndexOffset(header.MetaLength, header.PaletteLength);

                long cursor = EsrpFormat.DataStartOffset(header.MetaLength, header.PaletteLength, header.Capacity);
                if (written > 0)
                {
                    byte[] last = new byte[EsrpFormat.FrameIndexEntrySize];
                    ReadExactly(handle, last, indexOffset + (long)(written - 1) * EsrpFormat.FrameIndexEntrySize);
                    (long offset, int length) = EsrpFormat.ReadIndexEntry(last);
                    cursor = offset + length;
                }

                //drop whatever landed after the last indexed frame
                RandomAccess.SetLength(handle, cursor);

                return new EsrpWriter(handle, path, header, meta, palette, written, cursor);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        /// <summary>Appends the next frame's stored bytes (already encoded/compressed).</summary>
        public void Append(ReadOnlySpan<byte> stored)
        {
            if (FramesWritten >= Header.Capacity)
                throw new InvalidOperationException($"'{Path}' is full ({Header.Capacity} frames).");

            if (stored.IsEmpty)
                throw new ArgumentException("A stored frame can't be empty; a zero length means 'not written'.", nameof(stored));

            RandomAccess.Write(_handle, stored, _cursor);

            byte[] entry = new byte[EsrpFormat.FrameIndexEntrySize];
            EsrpFormat.WriteIndexEntry(entry, _cursor, stored.Length);
            RandomAccess.Write(_handle, entry, _indexOffset + (long)FramesWritten * EsrpFormat.FrameIndexEntrySize);

            _cursor += stored.Length;
            FramesWritten++;
        }

        /// <summary>Marks the proxy complete at however many frames were actually written.</summary>
        public void Complete()
        {
            byte[] tail = new byte[8];
            BitConverter.TryWriteBytes(tail.AsSpan(0, 4), EsrpFormat.CompleteFlag);
            BitConverter.TryWriteBytes(tail.AsSpan(4, 4), FramesWritten);

            //count first, flag with it in the same write: readers check the flag before trusting the count
            RandomAccess.Write(_handle, tail, EsrpFormat.FlagsOffset);
            RandomAccess.FlushToDisk(_handle);

            Header = Header with { Complete = true, FrameCount = FramesWritten };
        }

        private static void ReadExactly(SafeFileHandle handle, Span<byte> buffer, long offset)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = RandomAccess.Read(handle, buffer[total..], offset + total);
                if (read == 0) throw new InvalidDataException("Proxy file is truncated.");
                total += read;
            }
        }

        public void Dispose() => _handle.Dispose();
    }
}
