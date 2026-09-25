using System;
using System.IO;
using SkiaSharp;
using EditSharp.Caching.Proxy;
using EditSharp.Video;

namespace EditSharp.Components.Sources.Video;

/// <summary>A still image, decoded once and shared by every reader of the session. Stills have no proxy: every mode reads the original.</summary>
internal sealed class PreparedMediaImage(MediaVideoSource source, SKImage image, (int Width, int Height) nativeSize) : IPreparedVideoSource
{
    public (int Width, int Height) NativeSize => nativeSize;

    public IVideoFrameReader OpenReader(VideoReaderOptions options) => new Reader(source, image);

    public void Dispose() => image.Dispose();

    //a still is random-access in both modes; only the window can end it
    private sealed class Reader(MediaVideoSource source, SKImage image) : IVideoFrameReader
    {
        public VideoFrame GetFrame(TimeSpan contentTime)
        {
            source.MapTime(contentTime, null);
            return new VideoFrame(image, Transient: false);
        }

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
/// </summary>
internal sealed class PreparedMediaVideo(
    MediaVideoSource source, string path, MediaInfo info, DecodeHwAccelPlan plan, SourceMode mode, ProxyEntry? proxy)
    : IPreparedVideoSource
{
    public (int Width, int Height) NativeSize => (info.Width, info.Height);

    public IVideoFrameReader OpenReader(VideoReaderOptions options)
    {
        bool owns = options.CallerOwnsFrames;

        if (options.Mode == VideoReadMode.RandomAccess)
            return new ProxyVideoReader(source, path, info.Duration, proxy, sequential: false, owns);

        return mode switch
        {
            SourceMode.SourceOnly => Original(options),
            SourceMode.ProxiesOnly => new ProxyVideoReader(source, path, info.Duration, proxy, sequential: true, owns),
            SourceMode.ProxiesAndSource => new MixedMediaVideoReader(
                source, info.Duration, new ProxyVideoReader(source, path, info.Duration, proxy, sequential: true, owns),
                start => Original(options with { StartAt = start })),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
    }

    public void Dispose() { }

    /// <summary>The decode target is the size compositing asked for, capped at native size per axis.</summary>
    private SequentialMediaVideoReader Original(VideoReaderOptions options)
    {
        int width = options.MaxWidth > 0 ? Math.Min(options.MaxWidth, info.Width) : info.Width;
        int height = options.MaxHeight > 0 ? Math.Min(options.MaxHeight, info.Height) : info.Height;

        return new SequentialMediaVideoReader(source, info.Duration, new MediaDecodeTarget(path, width, height, plan), options);
    }
}

/// <summary>What a sequential reader of an original decodes: which file, at what size, how.</summary>
internal readonly record struct MediaDecodeTarget(string Path, int Width, int Height, DecodeHwAccelPlan Plan);

/// <summary>
/// Forward decoding of the original through one ffmpeg pipe (SourceDecoder).
/// Each decoded frame covers Speed/Fps of file time; the reader keeps the
/// current frame and hands it back (reader-owned) for every content time that
/// still lands inside it. Time is mapped through the source's live
/// Start/Duration/Loop on every call, so a trim edit or a loop wrap simply
/// shows up as a jump: backwards, or further ahead than is worth decoding
/// through, reopens the pipe at the new position.
///
/// With CallerOwnsFrames every frame is handed over and none kept; asking
/// for the frame just handed over again decodes it again.
/// </summary>
internal sealed class SequentialMediaVideoReader : IVideoFrameReader
{
    private static readonly TimeSpan MaxDecodeThrough = TimeSpan.FromSeconds(2);

    private readonly MediaVideoSource _source;
    private readonly TimeSpan? _naturalLength;
    private readonly MediaDecodeTarget _target;
    private readonly VideoReaderOptions _options;
    private readonly TimeSpan _frameStep;

    private SourceDecoder? _decoder;
    private TimeSpan _nextTime;

    private SKImage? _current;
    private TimeSpan _currentTime;

    public SequentialMediaVideoReader(MediaVideoSource source, TimeSpan? naturalLength, MediaDecodeTarget target, VideoReaderOptions options)
    {
        _source = source;
        _naturalLength = naturalLength;
        _target = target;
        _options = options;
        _frameStep = TimeSpan.FromSeconds(options.Speed / options.Fps);

        //start ffmpeg seeking now rather than on the first frame; a start
        //that's already past the end just waits for GetFrame to report it
        try { Reopen(_source.MapTime(options.StartAt, _naturalLength)); }
        catch (SourceUnavailableException ex) when (ex.Reason == SourceUnavailableReason.EndOfSource) { }
    }

    public VideoFrame GetFrame(TimeSpan contentTime)
    {
        TimeSpan time = _source.MapTime(contentTime, _naturalLength);

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
            _nextTime += _frameStep;

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

    private void Reopen(TimeSpan time)
    {
        _decoder?.Dispose();
        _current?.Dispose();
        _decoder = null;
        _current = null;

        try
        {
            _decoder = SourceDecoder.Start(
                _target.Path, time.TotalSeconds, _options.Fps, _target.Width, _target.Height,
                _target.Plan, fastOpen: false, _options.Speed);
        }
        catch (Exception ex)
        {
            throw Unavailable($"Could not start decoding '{_target.Path}'.", ex);
        }

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
internal sealed class ProxyVideoReader(
    MediaVideoSource source, string path, TimeSpan? naturalLength, ProxyEntry? entry, bool sequential, bool callerOwnsFrames)
    : IVideoFrameReader
{
    private static readonly TimeSpan LookupInterval = TimeSpan.FromMilliseconds(250);

    private ProxyEntry? _entry = entry;
    private IProxyFrames? _frames;
    private long _lastLookup;

    public VideoFrame GetFrame(TimeSpan contentTime)
    {
        TimeSpan time = source.MapTime(contentTime, naturalLength);

        return TryRead(time, out VideoFrame frame) switch
        {
            ProxyFrameAvailability.Ready => frame,
            ProxyFrameAvailability.PastEnd => throw new SourceUnavailableException(SourceUnavailableReason.EndOfSource,
                $"{contentTime} is past the end of the proxy for '{path}'."),
            _ => throw Pending(),
        };
    }

    /// <summary>The proxy frame at file time `sourceTime`, or why not; for callers (the mixed reader) that fall back rather than fail.</summary>
    public ProxyFrameAvailability TryRead(TimeSpan sourceTime, out VideoFrame frame)
    {
        frame = default;

        if (Frames() is not { } frames) return ProxyFrameAvailability.Pending;

        int index = (int)Math.Floor(sourceTime.TotalSeconds * frames.FrameRate + 1e-9);

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
            if (now - _lastLookup < LookupInterval.TotalMilliseconds) return null;
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
internal sealed class MixedMediaVideoReader(
    MediaVideoSource source, TimeSpan? naturalLength, ProxyVideoReader proxy, Func<TimeSpan, SequentialMediaVideoReader> openOriginal)
    : IVideoFrameReader
{
    private SequentialMediaVideoReader? _original;

    public VideoFrame GetFrame(TimeSpan contentTime)
    {
        TimeSpan time = source.MapTime(contentTime, naturalLength);

        if (proxy.TryRead(time, out VideoFrame frame) == ProxyFrameAvailability.Ready)
            return frame;

        _original ??= openOriginal(contentTime);
        return _original.GetFrame(contentTime);
    }

    public void Dispose()
    {
        proxy.Dispose();
        _original?.Dispose();
        _original = null;
    }
}
