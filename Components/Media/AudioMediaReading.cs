using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using EditSharp.Video;

namespace EditSharp.Components.Media
{
    /// <summary>A media file readied for one session: its probe. Every reader gets its own ffmpeg pipe.</summary>
    internal sealed class PreparedAudioFile(string path, MediaInfo info) : IPreparedAudioSource
    {
        public IAudioSampleReader OpenReader(AudioReaderOptions options) => new AudioFileReader(path, info, options);

        public void Dispose() { }
    }

    /// <summary>
    /// Streams one file's audio as interleaved float32 at the requested rate and
    /// channel count, through one ffmpeg pipe, from the file time the reader was
    /// opened at. A short read is the end of the file; reading again after it is
    /// EndOfSource. A file with no audio stream yields silence for its probed
    /// length. Windows and loops are the reading node's business: it opens a
    /// new reader wherever it needs to jump.
    /// </summary>
    internal sealed class AudioFileReader : IAudioSampleReader
    {
        private readonly string _path;
        private readonly MediaInfo _info;
        private readonly int _rate;
        private readonly int _channels;

        //file position, in frames
        private long _position;
        private bool _ended;

        private Process? _ffmpeg;
        private Stream? _pipe;
        private long _pipeFrame; //file frame the pipe delivers next

        public AudioFileReader(string path, MediaInfo info, AudioReaderOptions options)
        {
            _path = path;
            _info = info;
            _rate = options.SampleRate;
            _channels = options.Channels;
            _position = options.StartAt.ToSamples(_rate, Rounding.Nearest);
        }

        public int Read(Span<float> destination)
        {
            if (_ended)
                throw new SourceUnavailableException(SourceUnavailableReason.EndOfSource, $"'{_path}' has no more audio.");

            int wanted = destination.Length / _channels;
            int got = _info.HasAudio
                ? ReadFile(_position, destination[..(wanted * _channels)])
                : Silence(wanted, destination);

            _position += got;

            //a short read is the end; the next call reports it
            if (got < wanted) _ended = true;
            return got;
        }

        //silence up to the file's probed length, or without end when it has none
        private int Silence(int wanted, Span<float> destination)
        {
            int frames = wanted;

            if (_info.Duration is { } duration)
            {
                long total = duration.ToSamples(_rate);
                frames = (int)Math.Max(0, Math.Min(wanted, total - _position));
            }

            destination[..(frames * _channels)].Clear();
            return frames;
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
}
