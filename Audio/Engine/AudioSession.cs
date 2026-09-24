using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using EditSharp.Rendering;

namespace EditSharp.Audio
{
    /// <summary>
    /// One block of audio seen at a tap: interleaved float samples (valid only
    /// during the callback), their format, and the timeline position of the
    /// block's first frame.
    /// </summary>
    public readonly record struct AudioTapBlock(ReadOnlyMemory<float> Samples, int SampleRate, int Channels, TimeSpan Position);

    /// <summary>Well-known tap points besides node and channel ids.</summary>
    public static class AudioTap
    {
        /// <summary>The master output, after every channel is summed.</summary>
        public static readonly Guid Master = new("7a1f0b5e-3c2d-4e8f-9a6b-0d1c2e3f4a5b");
    }
}

namespace EditSharp.Audio.Engine
{
    /// <summary>
    /// What every processor in one audio session shares. WaitForSources is
    /// true for exports (a source still preparing is waited for) and false
    /// for previews (it plays as silence until ready). Report collects
    /// source failures in exports.
    /// </summary>
    internal sealed class AudioSession(AudioFormat format, bool waitForSources, RenderReportBuilder? report = null, AudioTaps? taps = null)
    {
        public AudioFormat Format { get; } = format;
        public int BlockFrames { get; } = EditSharpConfig.AudioBlockFrames;
        public bool WaitForSources { get; } = waitForSources;
        public RenderReportBuilder? Report { get; } = report;
        public AudioTaps Taps { get; } = taps ?? new();

        public long FrameOf(TimeSpan time) => (long)Math.Round(time.TotalSeconds * Format.SampleRate);

        public TimeSpan TimeOf(long frame) => TimeSpan.FromSeconds(frame / (double)Format.SampleRate);
    }

    /// <summary>Listeners on node, channel and master outputs; see Playback.TapAudio.</summary>
    internal sealed class AudioTaps
    {
        private readonly ConcurrentDictionary<Guid, ImmutableArray<Action<AudioTapBlock>>> _listeners = new();

        public bool Has(Guid id) => _listeners.ContainsKey(id);

        public IDisposable Add(Guid id, Action<AudioTapBlock> listener)
        {
            _listeners.AddOrUpdate(id, [listener], (_, existing) => existing.Add(listener));
            return new Subscription(() =>
            {
                while (_listeners.TryGetValue(id, out var existing))
                {
                    var remaining = existing.Remove(listener);
                    bool done = remaining.IsEmpty
                        ? _listeners.TryRemove(new KeyValuePair<Guid, ImmutableArray<Action<AudioTapBlock>>>(id, existing))
                        : _listeners.TryUpdate(id, remaining, existing);
                    if (done) return;
                }
            });
        }

        public void Publish(Guid id, float[] samples, int count, AudioFormat format, TimeSpan position)
        {
            if (!_listeners.TryGetValue(id, out var listeners)) return;

            var block = new AudioTapBlock(new ReadOnlyMemory<float>(samples, 0, count), format.SampleRate, format.Channels, position);
            foreach (Action<AudioTapBlock> listener in listeners)
            {
                try { listener(block); }
                catch (Exception ex) { EditSharpConfig.Logger.LogWarning($"An audio tap listener threw: {ex.Message}"); }
            }
        }

        private sealed class Subscription(Action dispose) : IDisposable
        {
            private Action? _dispose = dispose;
            public void Dispose() => System.Threading.Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }
}
