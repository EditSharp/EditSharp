using System;
using System.IO;
using SkiaSharp;
using EditSharp.Caching.Proxy;
using EditSharp.Video;

namespace EditSharp.Components.Media;

/// <summary>Frame-indexed access to one proxy, at the proxy's own frame rate, whatever its format.</summary>
internal interface IProxyFrames : IDisposable
{
    Rational FrameRate { get; }

    ProxyFrameAvailability TryGetFrame(int frame, out VideoFrame result);
}

internal static class ProxyFrames
{
    public static IProxyFrames Open(ProxyEntry entry, bool sequential, bool callerOwnsFrames) => entry.IsEsrp
        ? new EsrpProxyFrames(EsrpReader.Open(entry.Path))
        : new MovProxyFrames(entry, sequential, callerOwnsFrames);
}

/// <summary>
/// .esrp: every frame is a positioned read, in any order, in both modes;
/// loops and seeks never reopen anything. Frames are fresh copies the caller
/// owns.
/// </summary>
internal sealed class EsrpProxyFrames(EsrpReader reader) : IProxyFrames
{
    public Rational FrameRate => reader.FrameRate;

    public ProxyFrameAvailability TryGetFrame(int frame, out VideoFrame result)
    {
        ProxyFrameAvailability availability = reader.TryGetFrame(frame, out SKImage? image);
        result = availability == ProxyFrameAvailability.Ready ? new VideoFrame(image!, Transient: true) : default;
        return availability;
    }

    public void Dispose() => reader.Dispose();
}

/// <summary>
/// DNxHR/ProRes, read through ffmpeg. Random access decodes each frame with
/// a one-shot ffmpeg (cheap to seek on all-intra, but a process per frame).
/// Sequential keeps one pipe per file and reopens it on a jump (backwards,
/// more than two seconds ahead, or into another segment): the one exception
/// to "random-access media never reopens", since per-frame processes can't
/// keep up with playback.
///
/// While the proxy is building, its sidecar is re-read (throttled) whenever
/// a frame isn't covered yet, so newly written stretches (and the switch
/// from segments to the final file) are picked up without reopening.
/// </summary>
internal sealed class MovProxyFrames : IProxyFrames
{
    private static readonly Time ReloadInterval = Time.FromMilliseconds(250);
    private static readonly Time MaxDecodeThrough = Time.FromSeconds(2);

    private readonly ProxyEntry _entry;
    private readonly string _directory;
    private readonly bool _sequential;
    private readonly bool _callerOwnsFrames;
    private MovProxyMeta _meta;
    private long _lastReload;

    private SourceDecoder? _decoder;
    private string? _decoderFile;
    private int _nextFrame;
    private SKImage? _current;
    private int _currentFrame = -1;

    public MovProxyFrames(ProxyEntry entry, bool sequential, bool callerOwnsFrames)
    {
        _entry = entry;
        _directory = Path.GetDirectoryName(entry.Path)!;
        _sequential = sequential;
        _callerOwnsFrames = callerOwnsFrames;
        _meta = MovProxyMeta.Load(entry.Path);
        _lastReload = Environment.TickCount64;
    }

    public Rational FrameRate => _entry.FrameRate;

    public ProxyFrameAvailability TryGetFrame(int frame, out VideoFrame result)
    {
        result = default;

        if (Locate(frame) is not { } location)
            return _meta.Complete && frame >= _meta.TotalFrames ? ProxyFrameAvailability.PastEnd : ProxyFrameAvailability.Pending;

        if (!_sequential)
        {
            SKImage image = SourceDecoder.DecodeSingleFrameAsync(location.File, location.Time, _entry.Width, _entry.Height)
                .GetAwaiter().GetResult();
            result = new VideoFrame(image, Transient: true);
            return ProxyFrameAvailability.Ready;
        }

        if (_current is not null && frame == _currentFrame && location.File == _decoderFile)
        {
            result = new VideoFrame(_current, Transient: false);
            return ProxyFrameAvailability.Ready;
        }

        if (_decoder is null || location.File != _decoderFile || frame < _nextFrame ||
            Time.FromFrame(frame - _nextFrame, FrameRate) > MaxDecodeThrough)
            Reopen(location.File, location.Time, frame);

        while (true)
        {
            SKImage image = _decoder!.NextFrame();

            //a segment still being written can run dry right at its edge
            if (_decoder.IsExhausted)
            {
                image.Dispose();
                _decoder.Dispose();
                _decoder = null;
                return ProxyFrameAvailability.Pending;
            }

            _current?.Dispose();
            _current = null;
            _currentFrame = _nextFrame++;

            if (_currentFrame != frame)
            {
                image.Dispose();
                continue;
            }

            if (_callerOwnsFrames)
            {
                result = new VideoFrame(image, Transient: true);
                return ProxyFrameAvailability.Ready;
            }

            _current = image;
            result = new VideoFrame(_current, Transient: false);
            return ProxyFrameAvailability.Ready;
        }
    }

    private (string File, Time Time)? Locate(int frame)
    {
        if (_meta.Locate(frame, _directory) is { } found) return found;
        if (_meta.Complete) return null;

        long now = Environment.TickCount64;
        if (Time.FromMilliseconds(now - _lastReload) < ReloadInterval) return null;
        _lastReload = now;

        _meta = MovProxyMeta.Load(_entry.Path);
        return _meta.Locate(frame, _directory);
    }

    private void Reopen(string file, Time at, int frame)
    {
        _decoder?.Dispose();
        _current?.Dispose();
        _current = null;
        _currentFrame = -1;

        _decoder = SourceDecoder.Start(file, at, FrameRate, _entry.Width, _entry.Height);
        _decoderFile = file;
        _nextFrame = frame;
    }

    public void Dispose()
    {
        _decoder?.Dispose();
        _current?.Dispose();
        _decoder = null;
        _current = null;
    }
}
