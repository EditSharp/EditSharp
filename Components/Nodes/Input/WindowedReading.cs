using EditSharp.Components.Media;
using System;

namespace EditSharp.Components.Nodes.Input
{
    /// <summary>
    /// A prepared media's frames as an input node's content: every content time
    /// is mapped through the node's live Start/Duration/Loop before it reaches
    /// the media's reader, which works in the file's own time. A trim edit or a
    /// loop wrap therefore shows up to the media as a jump, which its reader
    /// handles as it would any seek.
    /// </summary>
    internal class WindowedVideo : IPreparedVideoSource
    {
        private readonly IPreparedVideoSource _inner;
        private readonly InputNode _node;
        private readonly Time? _naturalLength;

        private WindowedVideo(IPreparedVideoSource inner, InputNode node, Time? naturalLength)
        {
            _inner = inner;
            _node = node;
            _naturalLength = naturalLength;
        }

        /// <summary>Wraps a prepared media; one bound to the compositor stays bound.</summary>
        public static IPreparedVideoSource Wrap(IPreparedVideoSource inner, InputNode node, Time? naturalLength) =>
            inner is ICompositorBound ? new Bound(inner, node, naturalLength) : new WindowedVideo(inner, node, naturalLength);

        public (int Width, int Height) NativeSize => _inner.NativeSize;

        public IVideoFrameReader OpenReader(VideoReaderOptions options) => new Reader(_inner, _node, _naturalLength, options);

        public void Dispose() => _inner.Dispose();

        private sealed class Bound(IPreparedVideoSource inner, InputNode node, Time? naturalLength)
            : WindowedVideo(inner, node, naturalLength), ICompositorBound;

        //the media's reader is opened at the first mapped time: right away when the start is inside the
        //window, so decoding gets going before the first frame is asked for; otherwise on the first
        //GetFrame that maps, since one past the window has nothing to open for
        private sealed class Reader : IVideoFrameReader
        {
            private readonly IPreparedVideoSource _prepared;
            private readonly InputNode _node;
            private readonly Time? _naturalLength;
            private readonly VideoReaderOptions _options;
            private IVideoFrameReader? _inner;

            public Reader(IPreparedVideoSource prepared, InputNode node, Time? naturalLength, VideoReaderOptions options)
            {
                _prepared = prepared;
                _node = node;
                _naturalLength = naturalLength;
                _options = options;

                try { Open(node.ToMaterialTime(options.StartAt, naturalLength)); }
                catch (SourceUnavailableException ex) when (ex.Reason == SourceUnavailableReason.EndOfSource) { }
            }

            public VideoFrame GetFrame(Time contentTime)
            {
                Time time = _node.ToMaterialTime(contentTime, _naturalLength);
                if (_inner is null) Open(time);
                return _inner!.GetFrame(time);
            }

            private void Open(Time time) => _inner = _prepared.OpenReader(_options with { StartAt = time });

            public void Dispose() => _inner?.Dispose();
        }
    }

    /// <summary>
    /// A prepared media's samples as an input node's content. Position is kept
    /// in frames of content time and mapped through the node's live
    /// Start/Duration/Loop on every read; the media's reader works in the file's
    /// own time and is reopened wherever the mapped position jumps (a trim edit,
    /// a loop wrap). The window ends the stream, or loops it; the media running
    /// out before its probed length is its real end.
    /// </summary>
    internal sealed class WindowedAudio(IPreparedAudioSource inner, InputNode node, Time? naturalLength) : IPreparedAudioSource
    {
        public IAudioSampleReader OpenReader(AudioReaderOptions options) => new Reader(inner, node, naturalLength, options);

        public void Dispose() => inner.Dispose();

        private sealed class Reader : IAudioSampleReader
        {
            private readonly IPreparedAudioSource _prepared;
            private readonly InputNode _node;
            private readonly Time? _naturalLength;
            private readonly AudioReaderOptions _options;
            private readonly int _rate;
            private readonly int _channels;

            //content position, in frames since the in-point
            private long _position;
            private bool _ended;

            private IAudioSampleReader? _reader;
            private long _readerFrame; //file frame the reader delivers next

            public Reader(IPreparedAudioSource prepared, InputNode node, Time? naturalLength, AudioReaderOptions options)
            {
                _prepared = prepared;
                _node = node;
                _naturalLength = naturalLength;
                _options = options;
                _rate = options.SampleRate;
                _channels = options.Channels;
                _position = options.StartAt.ToSamples(_rate, Rounding.Nearest);
            }

            public int Read(Span<float> destination)
            {
                if (_ended)
                    throw new SourceUnavailableException(SourceUnavailableReason.EndOfSource, $"{_node.Description} has no more audio.");

                int wanted = destination.Length / _channels;
                int written = 0;

                while (written < wanted)
                {
                    (Time start, Time? length) = _node.ResolveWindow(_naturalLength);
                    long startFrame = start.ToSamples(_rate, Rounding.Nearest);
                    long? windowFrames = length is { } l ? l.ToSamples(_rate) : null;

                    if (windowFrames is { } frames && _position >= frames)
                    {
                        if (!_node.Loop || frames <= 0)
                        {
                            _ended = true;
                            break;
                        }

                        _position %= frames;
                    }

                    int chunk = wanted - written;
                    if (windowFrames is { } remaining) chunk = (int)System.Math.Min(chunk, remaining - _position);

                    Span<float> target = destination.Slice(written * _channels, chunk * _channels);
                    int got = ReadMaterial(startFrame + _position, target);

                    written += got;
                    _position += got;

                    //the material ran out before its probed length: that's its real end
                    if (got < chunk)
                    {
                        if (_node.Loop && _position > 0 && windowFrames is null or > 0)
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

            //up to target's frames from file frame `frame`, reopening the media's reader if it isn't already there
            private int ReadMaterial(long frame, Span<float> target)
            {
                if (_reader is null || frame != _readerFrame)
                {
                    _reader?.Dispose();
                    _reader = _prepared.OpenReader(_options with { StartAt = Time.FromSamples(frame, _rate) });
                    _readerFrame = frame;
                }

                int got;
                try
                {
                    got = _reader.Read(target);
                }
                catch (SourceUnavailableException ex) when (ex.Reason == SourceUnavailableReason.EndOfSource)
                {
                    got = 0;
                }

                _readerFrame += got;
                return got;
            }

            public void Dispose() => _reader?.Dispose();
        }
    }
}
