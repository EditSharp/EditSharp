using System;
using System.Reflection;
using System.Threading.Tasks;
using EditSharp.Components.Sources;
using EditSharp.Components.Sources.Audio;

namespace EditSharp.Audio.Engine
{
    /// <summary>
    /// An AudioSource as a content stream: prepared in the background as soon
    /// as the clip comes into view, read through one streaming reader, and
    /// re-opened at the new position on a seek. A source that fails reads as
    /// silence: an export records it in its report, a preview logs it once and
    /// tries again after EditSharpConfig.SourceRetryInterval.
    /// </summary>
    internal sealed class SourceContentAudio(AudioSource source, Guid nodeId, AudioSession session) : IContentAudio
    {
        private Task<IPreparedAudioSource>? _preparing;
        private IPreparedAudioSource? _prepared;
        private IAudioSampleReader? _reader;
        private long _position;
        private long _failedAt = -1;

        public AudioSource Source { get; } = source;

        public int Generation { get; private set; }

        public bool Ready(bool wait)
        {
            if (_prepared is not null) return true;
            if (_failedAt >= 0 && !RetryDue()) return true;

            _failedAt = -1;
            _preparing ??= Task.Run(() => Source.PrepareAsync());

            if (!_preparing.IsCompleted)
            {
                if (!wait) return false;
                try { _preparing.Wait(); } catch (AggregateException) { }
            }

            if (_preparing.IsCompletedSuccessfully)
            {
                _prepared = _preparing.Result;
                Generation++;
            }
            else
            {
                Fail(_preparing.Exception?.InnerException as SourceUnavailableException
                    ?? new SourceUnavailableException(SourceUnavailableReason.DecodeError, $"{Describe()} could not be prepared.", _preparing.Exception?.InnerException));
            }

            _preparing = null;
            return true;
        }

        public void Seek(long frame)
        {
            _reader?.Dispose();
            _reader = null;
            _position = Math.Max(0, frame);
        }

        public int Read(Span<float> destination)
        {
            if (_prepared is null) return 0;

            try
            {
                _reader ??= _prepared.OpenReader(new AudioReaderOptions(
                    session.Format.SampleRate, session.Format.Channels, session.TimeOf(_position)));

                int channels = session.Format.Channels;
                int total = 0;
                while (total * channels < destination.Length)
                {
                    int read = _reader.Read(destination[(total * channels)..]);
                    if (read == 0) break;
                    total += read;
                }

                _position += total;
                return total;
            }
            catch (SourceUnavailableException ex) when (ex.Reason == SourceUnavailableReason.EndOfSource)
            {
                return 0;
            }
            catch (SourceUnavailableException ex)
            {
                Fail(ex);
                return 0;
            }
        }

        private void Fail(SourceUnavailableException ex)
        {
            _failedAt = Environment.TickCount64;
            _reader?.Dispose();
            _reader = null;
            _prepared?.Dispose();
            _prepared = null;

            TimeSpan at = session.TimeOf(_position);
            if (session.Report is { } report)
            {
                if (report.Record(nodeId, Describe(), ex.Reason, ex.Message, at))
                    EditSharpConfig.Logger.LogWarning($"{Describe()}: {ex.Message} Rendering silence.");
            }
            else
            {
                EditSharpConfig.Logger.LogWarning($"{Describe()}: {ex.Message} Playing silence.");
            }
        }

        //exports never retry; previews do once the interval has passed
        private bool RetryDue() =>
            !session.WaitForSources &&
            ((long)EditSharpConfig.SourceRetryInterval.TotalMilliseconds <= Environment.TickCount64 - _failedAt);

        private string Describe()
        {
            string kind = Source.GetType().GetCustomAttribute<SourceKindAttribute>()?.Id ?? Source.GetType().Name;
            return Source is IFileBackedSource file ? $"{kind}: {file.FilePath}" : kind;
        }

        public void Dispose()
        {
            _reader?.Dispose();
            _prepared?.Dispose();
            _preparing?.ContinueWith(static t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
        }
    }
}
