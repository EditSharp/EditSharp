using System;
using EditSharp.Audio.Engine;

namespace EditSharp.Audio.Processors
{
    /// <summary>
    /// The processor behind every audio input node: reads the node's content
    /// stream through a ContentWarp at the clip's speed and pitch mode. `identity` is what the
    /// stream was made from (the node's Source);
    /// when it changes the stream is rebuilt.
    /// </summary>
    internal sealed class ContentInputProcessor(Func<object> identity, Func<IContentAudio> create, AudioSession session) : IAudioProcessor
    {
        private object? _identity;
        private ContentWarp? _warp;

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

            warp.Render(tick.ContentFrame, tick.ContentStep * tick.Format.SampleRate, tick.Frames, tick.Pitch, output);
        }

        private ContentWarp Warp()
        {
            object current = identity();

            if (_warp is null || !ReferenceEquals(current, _identity))
            {
                _warp?.Dispose();
                _warp = new ContentWarp(create(), session.Format);
                _identity = current;
            }

            return _warp;
        }

        public void Dispose() => _warp?.Dispose();
    }
}
