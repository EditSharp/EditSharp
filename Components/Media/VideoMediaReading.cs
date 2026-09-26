using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using EditSharp.Caching.Proxy;
using EditSharp.Video;

namespace EditSharp.Components.Media
{
    /// <summary>One frame read through a fresh prepare, for thumbnails and other one-shot callers.</summary>
    internal static class VideoFrames
    {
        /// <summary>Prepares, opens one reader at `time`, takes the frame and closes everything.</summary>
        public static Task<SKImage> ReadOnceAsync(
            Func<VideoPrepareContext, CancellationToken, Task<IPreparedVideoSource>> prepare,
            Time time, SourceMode mode, int maxWidth, int maxHeight, CancellationToken ct) => Task.Run(async () =>
        {
            using IPreparedVideoSource prepared = await prepare(new VideoPrepareContext(HardwareAccelerator.None, mode), ct);

            //proxies are random-access; originals are read from that point on
            VideoReadMode readMode = mode == SourceMode.ProxiesOnly ? VideoReadMode.RandomAccess : VideoReadMode.Sequential;

            using IVideoFrameReader reader = prepared.OpenReader(new VideoReaderOptions(
                readMode, time, Fps: 30, Speed: Rational.One, MaxWidth: maxWidth, MaxHeight: maxHeight, CallerOwnsFrames: true));

            VideoFrame frame = reader.GetFrame(time);
            return frame.Transient ? frame.Image : CopyOf(frame.Image);
        }, ct);

        /// <summary>A raster copy the caller can own, for a frame the reader keeps.</summary>
        public static SKImage CopyOf(SKImage image)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Premul));

            if (!image.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes))
                throw new InvalidOperationException("Could not copy the frame's pixels.");

            return SKImage.FromBitmap(bitmap);
        }
    }

    /// <summary>A still image, decoded once and shared by every reader of the session. Stills have no proxy: every mode reads the original.</summary>
    internal sealed class PreparedStill(SKImage image, (int Width, int Height) nativeSize) : IPreparedVideoSource
    {
        public (int Width, int Height) NativeSize => nativeSize;

        public IVideoFrameReader OpenReader(VideoReaderOptions options) => new Reader(image);

        public void Dispose() => image.Dispose();

        //a still is the same picture at every time
        private sealed class Reader(SKImage image) : IVideoFrameReader
        {
            public VideoFrame GetFrame(Time time) => new(image, Transient: false);

            public void Dispose() { }
        }
    }

    /// <summary>
    /// A video file readied for one session: its probe, the hardware decode
    /// plan for the original, the session's SourceMode and the proxy known at
    /// prepare time (readers keep looking for one if there wasn't). Which reader
    /// a request gets:
    ///   random access        → the proxy, in every mode
    ///   sequential, SourceOnly       → the original, through ffmpeg
    ///   sequential, ProxiesOnly      → the proxy
    ///   sequential, ProxiesAndSource → the proxy where it covers the frame, else the original
    /// Every reader takes time in the file.
    /// </summary>
    internal sealed class PreparedVideoFile(string path, MediaInfo info, DecodeHwAccelPlan plan, SourceMode mode, ProxyEntry? proxy)
        : IPreparedVideoSource
    {
        public (int Width, int Height) NativeSize => (info.Width, info.Height);

        public IVideoFrameReader OpenReader(VideoReaderOptions options)
        {
            bool owns = options.CallerOwnsFrames;

            if (options.Mode == VideoReadMode.RandomAccess)
                return new ProxyVideoFileReader(path, proxy, sequential: false, owns);

            return mode switch
            {
                SourceMode.SourceOnly => Original(options),
                SourceMode.ProxiesOnly => new ProxyVideoFileReader(path, proxy, sequential: true, owns),
                SourceMode.ProxiesAndSource => new MixedVideoFileReader(
                    new ProxyVideoFileReader(path, proxy, sequential: true, owns),
                    start => Original(options with { StartAt = start })),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
            };
        }

        public void Dispose() { }

        /// <summary>The decode target is the size compositing asked for, capped at native size per axis.</summary>
        private SequentialVideoFileReader Original(VideoReaderOptions options)
        {
            int width = options.MaxWidth > 0 ? Math.Min(options.MaxWidth, info.Width) : info.Width;
            int height = options.MaxHeight > 0 ? Math.Min(options.MaxHeight, info.Height) : info.Height;

            return new SequentialVideoFileReader(new MediaDecodeTarget(path, width, height, plan), options);
        }
    }

    /// <summary>What a sequential reader of an original decodes: which file, at what size, how.</summary>
    internal readonly record struct MediaDecodeTarget(string Path, int Width, int Height, DecodeHwAccelPlan Plan);

    /// <summary>
    /// Forward decoding of the original through one ffmpeg pipe (SourceDecoder).
    /// Frame k after a seek starts at k × Speed/Fps of file time past it; the reader keeps the
    /// current frame and hands it back (reader-owned) for every time that still
    /// lands inside it. A jump backwards, or further ahead than is worth decoding
    /// through, reopens the pipe at the new position; the node reading through
    /// this maps a trim edit or a loop wrap into exactly such a jump.
    ///
    /// With CallerOwnsFrames every frame is handed over and none kept; asking
    /// for the frame just handed over again decodes it again.
    /// </summary>
    internal sealed class SequentialVideoFileReader : IVideoFrameReader
    {
        private static readonly Time MaxDecodeThrough = Time.FromSeconds(2);

        private readonly MediaDecodeTarget _target;
        private readonly VideoReaderOptions _options;
        private readonly Rational _frameSeconds;

        private SourceDecoder? _decoder;
        private Time _openedAt;
        private long _decoded;
        private Time _nextTime;

        private SKImage? _current;
        private Time _currentTime;

        public SequentialVideoFileReader(MediaDecodeTarget target, VideoReaderOptions options)
        {
            _target = target;
            _options = options;
            _frameSeconds = options.Speed / options.Fps;

            //start ffmpeg seeking now rather than on the first frame
            Reopen(options.StartAt);
        }

        public VideoFrame GetFrame(Time time)
        {
            if (_current is not null && time >= _currentTime && time < _nextTime)
                return new VideoFrame(_current, Transient: false);

            if (_decoder is null || time < _nextTime || time - _nextTime > MaxDecodeThrough)
                Reopen(time);

            while (true)
            {
                SKImage frame = Decode();

                _current?.Dispose();
                _current = null;
                _currentTime = _nextTime;
                _decoded++;
                _nextTime = _openedAt + Time.FromSeconds(_frameSeconds * _decoded);

                if (time >= _nextTime)
                {
                    frame.Dispose();
                    continue;
                }

                if (_options.CallerOwnsFrames)
                    return new VideoFrame(frame, Transient: true);

                _current = frame;
                return new VideoFrame(_current, Transient: false);
            }
        }

        private SKImage Decode()
        {
            SKImage frame;

            try
            {
                frame = _decoder!.NextFrame();
            }
            catch (Exception ex)
            {
                throw Unavailable($"Decoding '{_target.Path}' failed.", ex);
            }

            //the file ran out of frames before its probed length did
            if (_decoder.IsExhausted)
            {
                frame.Dispose();
                throw new SourceUnavailableException(SourceUnavailableReason.EndOfSource,
                    $"'{_target.Path}' ran out of frames at {_nextTime}.");
            }

            return frame;
        }

        private void Reopen(Time time)
        {
            _decoder?.Dispose();
            _current?.Dispose();
            _decoder = null;
            _current = null;

            try
            {
                _decoder = SourceDecoder.Start(
                    _target.Path, time, _options.Fps, _target.Width, _target.Height,
                    _target.Plan, fastOpen: false, _options.Speed);
            }
            catch (Exception ex)
            {
                throw Unavailable($"Could not start decoding '{_target.Path}'.", ex);
            }

            _openedAt = time;
            _decoded = 0;
            _nextTime = time;
        }

        private SourceUnavailableException Unavailable(string message, Exception inner) => new(
            File.Exists(_target.Path) ? SourceUnavailableReason.DecodeError : SourceUnavailableReason.MediaOffline,
            message, inner);

        public void Dispose()
        {
            _decoder?.Dispose();
            _current?.Dispose();
            _decoder = null;
            _current = null;
        }
    }

    /// <summary>
    /// Reads a file's proxy, whatever its format (see ProxyFrames). If none was
    /// known when the session prepared, it keeps checking ProxyCache (a cheap
    /// in-memory lookup, throttled) so a proxy built mid-session is picked up.
    /// A frame the proxy doesn't cover yet is ProxyPending while a build is
    /// queued or running and ProxyMissing otherwise; past a complete
    /// proxy's end is EndOfSource.
    /// </summary>
    internal sealed class ProxyVideoFileReader(string path, ProxyEntry? entry, bool sequential, bool callerOwnsFrames)
        : IVideoFrameReader
    {
        private static readonly Time LookupInterval = Time.FromMilliseconds(250);

        private ProxyEntry? _entry = entry;
        private IProxyFrames? _frames;
        private long _lastLookup;

        public VideoFrame GetFrame(Time time) => TryRead(time, out VideoFrame frame) switch
        {
            ProxyFrameAvailability.Ready => frame,
            ProxyFrameAvailability.PastEnd => throw new SourceUnavailableException(SourceUnavailableReason.EndOfSource,
                $"{time} is past the end of the proxy for '{path}'."),
            _ => throw Pending(),
        };

        /// <summary>The proxy frame at file time `time`, or why not; for callers (the mixed reader) that fall back rather than fail.</summary>
        public ProxyFrameAvailability TryRead(Time time, out VideoFrame frame)
        {
            frame = default;

            if (Frames() is not { } frames) return ProxyFrameAvailability.Pending;

            int index = (int)time.ToFrame(frames.FrameRate);

            try
            {
                return frames.TryGetFrame(index, out frame);
            }
            catch (Exception ex) when (ex is not SourceUnavailableException)
            {
                throw new SourceUnavailableException(SourceUnavailableReason.DecodeError, $"Reading the proxy for '{path}' failed.", ex);
            }
        }

        private IProxyFrames? Frames()
        {
            if (_frames is not null) return _frames;

            if (_entry is null)
            {
                long now = Environment.TickCount64;
                if (Time.FromMilliseconds(now - _lastLookup) < LookupInterval) return null;
                _lastLookup = now;

                if (!ProxyCache.TryGetEntry(path, out ProxyEntry found)) return null;
                _entry = found;
            }

            try
            {
                return _frames = ProxyFrames.Open(_entry, sequential, callerOwnsFrames);
            }
            catch (Exception ex)
            {
                throw new SourceUnavailableException(SourceUnavailableReason.DecodeError, $"Could not open the proxy for '{path}'.", ex);
            }
        }

        private SourceUnavailableException Pending() => File.Exists(path)
            ? ProxyCache.IsBuilding(path)
                ? new SourceUnavailableException(SourceUnavailableReason.ProxyPending, $"The proxy for '{path}' is still being built.")
                : new SourceUnavailableException(SourceUnavailableReason.ProxyMissing, $"'{path}' has no proxy for this frame, and none is being built.")
            : new SourceUnavailableException(SourceUnavailableReason.MediaOffline, $"'{path}' is missing.");

        public void Dispose()
        {
            _frames?.Dispose();
            _frames = null;
        }
    }

    /// <summary>
    /// ProxiesAndSource, decided frame by frame: the proxy wherever it has the
    /// frame, the original everywhere else. The original's pipe is opened only
    /// when first needed, at that frame, and thereafter follows its own reseek
    /// rules; so a clip that starts on the proxy and runs past a still-building
    /// proxy's edge continues on the original, and moves back onto the proxy on
    /// a later jump into covered ground.
    /// </summary>
    internal sealed class MixedVideoFileReader(ProxyVideoFileReader proxy, Func<Time, SequentialVideoFileReader> openOriginal)
        : IVideoFrameReader
    {
        private SequentialVideoFileReader? _original;

        public VideoFrame GetFrame(Time time)
        {
            if (proxy.TryRead(time, out VideoFrame frame) == ProxyFrameAvailability.Ready)
                return frame;

            _original ??= openOriginal(time);
            return _original.GetFrame(time);
        }

        public void Dispose()
        {
            proxy.Dispose();
            _original?.Dispose();
            _original = null;
        }
    }
}
