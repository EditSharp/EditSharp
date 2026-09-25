using System;
using System.Threading;
using System.Threading.Tasks;
using EditSharp.Audio.Engine;
using EditSharp.Editing;
using EditSharp.History;

namespace EditSharp.Components.Sources.Audio;

/// <summary>The shape of a tone's wave.</summary>
public enum Waveform
{
    /// <summary>A pure tone.</summary>
    Sine,

    /// <summary>Alternates between full positive and full negative.</summary>
    Square,

    /// <summary>Ramps up, then drops.</summary>
    Sawtooth,

    /// <summary>Ramps up, then down.</summary>
    Triangle,
}

/// <summary>A synthesized tone.</summary>
/// <remarks>Frequency and amplitude are evaluated every sample, and the phase carries on across blocks, so a changing frequency never clicks.</remarks>
[SourceKind("tone", DisplayName = "Tone")]
public class ToneAudioSource : AudioSource
{
    Waveform _waveform = Waveform.Sine;
    /// <summary>The shape of the wave.</summary>
    [Editable("Waveform")]
    public Waveform Waveform { get => _waveform; set => Transaction.Set(this, ref _waveform, value, static (o, v) => o._waveform = v); }

    Animatable<float> _frequency = new(440f);
    /// <summary>The pitch, in hertz; 440 by default.</summary>
    [Editable("Frequency", Min = 20, Max = 20000, Step = 1, Unit = "Hz")]
    public Animatable<float> Frequency { get => _frequency; set => Transaction.Set(this, ref _frequency, value, static (o, v) => o._frequency = v); }

    Animatable<float> _amplitude = new(1f);
    /// <summary>The volume, from 0 (silent) to 1 (full scale).</summary>
    [Editable("Amplitude", Min = 0, Max = 1, Step = 0.01)]
    public Animatable<float> Amplitude { get => _amplitude; set => Transaction.Set(this, ref _amplitude, value, static (o, v) => o._amplitude = v); }

    /// <inheritdoc/>
    public override System.Collections.Generic.IEnumerable<IAnimatable> Animatables => [Frequency, Amplitude];

    /// <inheritdoc/>
    public override ToneAudioSource Duplicate() => (ToneAudioSource)base.Duplicate();

    /// <summary>Always null: a tone has no end of its own.</summary>
    /// <param name="ct">Unused.</param>
    /// <returns>Null.</returns>
    public override Task<TimeSpan?> GetNaturalLengthAsync(CancellationToken ct = default) => Task.FromResult<TimeSpan?>(null);

    internal override Task<IPreparedAudioSource> PrepareAsync(CancellationToken ct = default) =>
        Task.FromResult<IPreparedAudioSource>(new Prepared(this));

    internal override void AddFingerprint(ref HashCode hash)
    {
        base.AddFingerprint(ref hash);
        hash.Add(Waveform);
    }

    private sealed class Prepared(ToneAudioSource source) : IPreparedAudioSource
    {
        public IAudioSampleReader OpenReader(AudioReaderOptions options) => new Reader(source, options);

        public void Dispose() { }
    }

    /// <summary>Content frames from StartAt on; Duration ends it (or loops it, keeping phase), as for any source.</summary>
    private sealed class Reader : IAudioSampleReader
    {
        private const double TwoPi = 2.0 * Math.PI;

        private readonly ToneAudioSource _source;
        private readonly int _rate, _channels;
        private long _position;
        private double _phase;
        private bool _ended;

        public Reader(ToneAudioSource source, AudioReaderOptions options)
        {
            _source = source;
            _rate = options.SampleRate;
            _channels = options.Channels;
            _position = (long)Math.Round(options.StartAt.TotalSeconds * _rate);

            //where a steady tone at the starting frequency would be
            double t = _position / (double)_rate;
            _phase = TwoPi * source.Frequency.Evaluate(options.StartAt) * t % TwoPi;
        }

        public int Read(Span<float> destination)
        {
            if (_ended) throw new SourceUnavailableException(SourceUnavailableReason.EndOfSource, "The tone has ended.");

            long? window = _source.ResolveWindow(null).Length is { } d ? (long)Math.Floor(d.TotalSeconds * _rate) : null;
            int frames = destination.Length / _channels;
            Waveform waveform = _source.Waveform;

            for (int i = 0; i < frames; i++)
            {
                if (window is { } w && _position >= w)
                {
                    if (!_source.Loop || w <= 0) { _ended = true; return i; }
                    _position %= w;
                }

                TimeSpan t = TimeSpan.FromSeconds(_position / (double)_rate);
                double value = waveform switch
                {
                    Waveform.Sine => Math.Sin(_phase),
                    Waveform.Square => Math.Sin(_phase) >= 0 ? 1.0 : -1.0,
                    Waveform.Sawtooth => 2.0 * ((_phase / TwoPi) - Math.Floor((_phase / TwoPi) + 0.5)),
                    Waveform.Triangle => (2.0 / Math.PI) * Math.Asin(Math.Sin(_phase)),
                    _ => 0.0,
                };

                float sample = (float)(value * _source.Amplitude.Evaluate(t));
                for (int ch = 0; ch < _channels; ch++) destination[i * _channels + ch] = sample;

                _phase += TwoPi * _source.Frequency.Evaluate(t) / _rate;
                if (_phase > TwoPi) _phase %= TwoPi;
                _position++;
            }

            return frames;
        }

        public void Dispose() { }
    }
}

/// <summary>Another timeline's mixed audio.</summary>
/// <remarks>The nested timeline is mixed block by block by its own TimelineAudioRenderer, in the reading session. It's saved as the timeline's Id; see <see cref="SourceSerializer.Deserialize"/>.</remarks>
//unlisted until the GUI has a timeline picker
[SourceKind("timeline-audio", DisplayName = "Timeline", Listed = false)]
public class TimelineAudioSource : AudioSource
{
    Timeline? _timeline;
    /// <summary>The timeline to play; null plays nothing and reports the source offline.</summary>
    [Editable("Timeline")]
    public Timeline? Timeline { get => _timeline; set { Transaction.Set(this, ref _timeline, value, static (o, v) => o._timeline = v); EndMayHaveMoved(); } }

    /// <inheritdoc/>
    public override TimelineAudioSource Duplicate() => (TimelineAudioSource)base.Duplicate();

    /// <summary>The timeline's duration.</summary>
    /// <param name="ct">Unused.</param>
    /// <returns>The duration; null when no timeline is chosen.</returns>
    public override Task<TimeSpan?> GetNaturalLengthAsync(CancellationToken ct = default) => Task.FromResult(Timeline?.Duration);

    internal override Task<IPreparedAudioSource> PrepareAsync(CancellationToken ct = default) => Timeline is { } timeline
        ? Task.FromResult<IPreparedAudioSource>(new Prepared(this, timeline))
        : Task.FromException<IPreparedAudioSource>(new SourceUnavailableException(SourceUnavailableReason.MediaOffline, "No timeline is chosen."));

    private protected override Timeline? ReferencedTimeline(Guid id) => Timeline?.Id == id ? Timeline : null;

    internal override void AddFingerprint(ref HashCode hash)
    {
        base.AddFingerprint(ref hash);
        hash.Add(Timeline?.Id);
    }

    private sealed class Prepared(TimelineAudioSource source, Timeline timeline) : IPreparedAudioSource
    {
        public IAudioSampleReader OpenReader(AudioReaderOptions options) => new Reader(source, timeline, options);

        public void Dispose() { }
    }

    private sealed class Reader : IAudioSampleReader
    {
        private readonly TimelineAudioSource _source;
        private readonly Timeline _timeline;
        private readonly AudioSession _session;
        private readonly TimelineAudioRenderer _renderer;
        private long _position;
        private bool _ended;

        public Reader(TimelineAudioSource source, Timeline timeline, AudioReaderOptions options)
        {
            _source = source;
            _timeline = timeline;

            //the reading session's taps, report and wait policy reach into the nested timeline
            _session = options.Session ?? new AudioSession(new AudioFormat(options.SampleRate, options.Channels), waitForSources: true);
            _renderer = new TimelineAudioRenderer(timeline, _session, isMaster: false);
            _position = _session.FrameOf(options.StartAt);
        }

        public int Read(Span<float> destination)
        {
            if (_ended) throw new SourceUnavailableException(SourceUnavailableReason.EndOfSource, "The nested timeline has ended.");

            int channels = _session.Format.Channels;
            int frames = destination.Length / channels;
            (TimeSpan from, TimeSpan? length) = _source.ResolveWindow(_timeline.Duration);
            long start = _session.FrameOf(from);
            long window = _session.FrameOf(length ?? TimeSpan.Zero);

            int done = 0;
            while (done < frames)
            {
                if (_position >= window)
                {
                    if (!_source.Loop || window <= 0) { _ended = true; break; }
                    _position %= window;
                }

                int block = (int)Math.Min(Math.Min(_session.BlockFrames, frames - done), window - _position);
                _renderer.Render(start + _position, destination.Slice(done * channels, block * channels));
                done += block;
                _position += block;
            }

            return done;
        }

        public void Dispose() => _renderer.Dispose();
    }
}
