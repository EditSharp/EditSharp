using System;
using System.IO;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using SkiaSharp;

namespace EditSharp.Composite
{
    /// <summary>
    /// Reads one .esrp scrub-proxy file (see ScrubProxyFormat) — no ffmpeg,
    /// no decode, no child process at all. GetFrameAt is pure arithmetic
    /// (which fixed-size frame slot a timestamp falls in) plus one direct
    /// positioned read via System.IO.RandomAccess, which reads at an
    /// explicit offset (pread on Unix, an OVERLAPPED ReadFile on Windows)
    /// rather than seek-then-read on a shared stream position — this is
    /// what lets GetFrameAt be genuinely safe to call back-to-back as fast
    /// as a caller likes, with no per-call process, no per-call I/O wait
    /// beyond one small positioned read, and (unlike a shared FileStream's
    /// Seek+Read) no shared cursor state a second concurrent caller could
    /// race against.
    ///
    /// ONE READER PER SOURCE, OWNED BY ITS ScrubFrameSource/caller — not a
    /// process-wide singleton. The one shared mutable field (_frameBuffer)
    /// is reused across calls to avoid a fresh allocation every scrub tick;
    /// see ScrubFrameSource's own remarks on why multi-clip prefetch is
    /// still sequential, not parallel, in this pass — that's what keeps
    /// reusing this single buffer safe without a lock.
    /// </summary>
    internal sealed class ScrubProxyReader : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly byte[] _frameBuffer;
        private readonly int _frameByteSize;

        public string Path { get; }
        public int Width { get; }
        public int Height { get; }
        public double SampleRate { get; }
        public int FrameCount { get; }

        private ScrubProxyReader(SafeFileHandle handle, string path, ScrubProxyFormat.Header header)
        {
            _handle = handle;
            Path = path;
            Width = header.Width;
            Height = header.Height;
            SampleRate = header.SampleRate;
            FrameCount = header.FrameCount;
            _frameByteSize = header.Width * header.Height * 4;
            _frameBuffer = new byte[_frameByteSize];
        }

        public static ScrubProxyReader Open(string path)
        {
            // FileOptions.RandomAccess is a hint to the OS cache/readahead
            // strategy (this file is never read sequentially), not a
            // correctness requirement — RandomAccess.Read below works
            // regardless, but the hint is free and matches actual usage.
            SafeFileHandle handle = File.OpenHandle(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);

            try
            {
                byte[] headerBytes = new byte[ScrubProxyFormat.HeaderSize];
                int read = RandomAccess.Read(handle, headerBytes, 0);
                if (read != headerBytes.Length)
                    throw new InvalidDataException($"'{path}' is truncated — could not read its scrub-proxy header.");

                ScrubProxyFormat.Header header = ScrubProxyFormat.ReadHeader(headerBytes, path);
                return new ScrubProxyReader(handle, path, header);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
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
        /// this method overwrites.
        /// </summary>
        public SKImage GetFrameAt(double seconds)
        {
            int index = (int)Math.Floor(seconds * SampleRate);
            index = Math.Clamp(index, 0, FrameCount - 1);

            long offset = ScrubProxyFormat.HeaderSize + (long)index * _frameByteSize;

            int totalRead = 0;
            while (totalRead < _frameByteSize)
            {
                int read = RandomAccess.Read(_handle, _frameBuffer.AsSpan(totalRead), offset + totalRead);
                if (read == 0) break; // real EOF — shouldn't happen against a well-formed file, tolerated not assumed
                totalRead += read;
            }

            if (totalRead != _frameByteSize)
                throw new InvalidOperationException(
                    $"ScrubProxyReader('{Path}') read {totalRead}/{_frameByteSize} bytes for frame " +
                    $"{index} at offset {offset} — the file may be truncated or corrupt.");

            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            SKData data = SKData.CreateCopy(_frameBuffer);
            return SKImage.FromPixels(info, data, Width * 4);
        }

        public void Dispose() => _handle.Dispose();
    }
}