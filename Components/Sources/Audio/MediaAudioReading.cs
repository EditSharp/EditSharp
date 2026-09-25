using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using EditSharp.Video;

namespace EditSharp.Components.Sources.Audio;

/// <summary>A media file readied for one session: its probe. Every reader gets its own ffmpeg pipe.</summary>
internal sealed class PreparedMediaAudio(MediaAudioSource source, string path, MediaInfo info) : IPreparedAudioSource
{
    public IAudioSampleReader OpenReader(AudioReaderOptions options) => new MediaAudioReader(source, path, info, options);

    public void Dispose() { }
}

/// <summary>
/// Streams one file's audio as interleaved float32 at the requested rate and
/// channel count, through one ffmpeg pipe. Position is kept in frames of
/// content time and mapped through the source's live Start/Duration/Loop on
/// every read, so a trim edit or a loop wrap shows up as a jump in file time,
/// which reopens the pipe there. A file with no audio stream yields silence.
/// </summary>
internal sealed class MediaAudioReader : IAudioSampleReader
{
    private readonly MediaAudioSource _source;
    private readonly string _path;
    private readonly MediaInfo _info;
    private readonly int _rate;
    private readonly int _channels;

    //content position, in frames since the in-point
    private long _position;
    private bool _ended;

    private Process? _ffmpeg;
    private Stream? _pipe;
    private long _pipeFrame; //file frame the pipe delivers next

    public MediaAudioReader(MediaAudioSource source, string path, MediaInfo info, AudioReaderOptions options)
    {
        _source = source;
        _path = path;
        _info = info;
        _rate = options.SampleRate;
        _channels = options.Channels;
        _position = (long)Math.Round(options.StartAt.TotalSeconds * _rate);
    }

    public int Read(Span<float> destination)
    {
        if (_ended)
            throw new SourceUnavailableException(SourceUnavailableReason.EndOfSource, $"'{_path}' has no more audio.");

        int wanted = destination.Length / _channels;
        int written = 0;

        while (written < wanted)
        {
            (TimeSpan start, TimeSpan? length) = _source.Window(_info.Duration);
            long startFrame = (long)Math.Round(start.TotalSeconds * _rate);
            long? windowFrames = length is { } l ? (long)Math.Floor(l.TotalSeconds * _rate) : null;

            if (windowFrames is { } frames && _position >= frames)
            {
                if (!_source.Loop || frames <= 0)
                {
                    _ended = true;
                    break;
                }

                _position %= frames;
            }

            int chunk = wanted - written;
            if (windowFrames is { } remaining) chunk = (int)Math.Min(chunk, remaining - _position);

            Span<float> target = destination.Slice(written * _channels, chunk * _channels);
            int got = _info.HasAudio ? ReadFile(startFrame + _position, target) : Silence(target);

            written += got;
            _position += got;

            //the file ran out before its probed length: that's its real end
            if (got < chunk)
            {
                if (_source.Loop && _position > 0 && windowFrames is null or > 0)
                {
                    _position = 0;
                    continue;
                }

                _ended = true;
                break;
            }
        }

        //a short read is the end; the next call reports it
        return written;
    }

    private static int Silence(Span<float> target)
    {
        target.Clear();
        return target.Length;
    }

    /// <summary>Reads up to target's frames starting at file frame `frame`, reopening the pipe if it isn't already there.</summary>
    private int ReadFile(long frame, Span<float> target)
    {
        if (_pipe is null || frame != _pipeFrame) Open(frame);

        Span<byte> bytes = MemoryMarshal.AsBytes(target);
        int bytesPerFrame = _channels * sizeof(float);
        int total = 0;

        try
        {
            while (total < bytes.Length)
            {
                int read = _pipe!.Read(bytes[total..]);
                if (read == 0) break;
                total += read;
            }
        }
        catch (IOException ex)
        {
            throw new SourceUnavailableException(SourceUnavailableReason.DecodeError, $"Decoding audio from '{_path}' failed.", ex);
        }

        int frames = total / bytesPerFrame;
        _pipeFrame += frames;
        return frames;
    }

    private void Open(long frame)
    {
        Close();

        var psi = new ProcessStartInfo
        {
            FileName = EditSharpConfig.FfmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        string seek = (frame / (double)_rate).ToString("R", CultureInfo.InvariantCulture);

        foreach (string arg in new[]
        {
            "-v", "error", "-ss", seek, "-i", _path, "-vn",
            "-ar", _rate.ToString(CultureInfo.InvariantCulture),
            "-ac", _channels.ToString(CultureInfo.InvariantCulture),
            "-f", "f32le", "pipe:1",
        })
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            _ffmpeg = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
            _ffmpeg.ErrorDataReceived += (_, _) => { };
            _ffmpeg.BeginErrorReadLine();
            _pipe = _ffmpeg.StandardOutput.BaseStream;
            _pipeFrame = frame;
        }
        catch (Exception ex)
        {
            Close();
            throw new SourceUnavailableException(
                File.Exists(_path) ? SourceUnavailableReason.DecodeError : SourceUnavailableReason.MediaOffline,
                $"Could not start decoding audio from '{_path}'.", ex);
        }
    }

    private void Close()
    {
        _pipe = null;

        if (_ffmpeg is null) return;

        try { if (!_ffmpeg.HasExited) _ffmpeg.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }

        _ffmpeg.Dispose();
        _ffmpeg = null;
    }

    public void Dispose() => Close();
}
