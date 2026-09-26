using System;
using EditSharp.Audio.Engine;

namespace EditSharp.Audio.Processors
{
    /// <summary>
    /// The processor behind every audio input node: reads the node's content
    /// stream through a ContentWarp at the clip's speed and pitch mode, or a
    /// reversed view of it through a second warp when the clip plays backwards.
    /// `identity` is what the stream was made from (the node's Source); when it
    /// changes the streams are rebuilt.
    /// </summary>
    internal sealed class ContentInputProcessor(Func<object> identity, Func<IContentAudio> create, AudioSession session) : IAudioProcessor
    {
        private object? _identity;
        private ContentWarp? _warp;
        private ContentWarp? _reverseWarp;

        /// <summary>Starts preparing ahead of time (with `wait`, until done); true once reads can start.</summary>
        public bool Prepare(bool wait = false) => Warp().Content.Ready(wait);

        public void Process(in AudioTick tick, AudioPortBuffers ports)
        {
            Span<float> output = ports.Outputs[0].AsSpan(0, tick.Samples);
            ContentWarp warp = Warp();

            //a preview doesn't wait: silence until the material is ready
            if (!warp.Content.Ready(session.WaitForSources))
            {
                output.Clear();
                return;
            }

            if (!tick.Reversed)
            {
                warp.Render(tick.ContentFrame, tick.Speed.Value, tick.Frames, tick.Pitch, output);
                return;
            }

            //the reversed view's frames are negated content frames, read forwards
            _reverseWarp ??= new ContentWarp(new ReversedContentAudio(create(), session.Format.Channels), session.Format);
            if (!_reverseWarp.Content.Ready(session.WaitForSources))
            {
                output.Clear();
                return;
            }

            _reverseWarp.Render(-tick.ContentFrame, tick.Speed.Value, tick.Frames, tick.Pitch, output);
        }

        private ContentWarp Warp()
        {
            object current = identity();

            if (_warp is null || !ReferenceEquals(current, _identity))
            {
                _warp?.Dispose();
                _reverseWarp?.Dispose();
                _reverseWarp = null;
                _warp = new ContentWarp(create(), session.Format);
                _identity = current;
            }

            return _warp;
        }

        public void Dispose()
        {
            _warp?.Dispose();
            _reverseWarp?.Dispose();
        }
    }
}
