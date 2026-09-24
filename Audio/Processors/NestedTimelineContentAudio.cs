using System;
using EditSharp.Audio.Engine;
using EditSharp.Components;
using EditSharp.Components.Clips;

namespace EditSharp.Audio.Processors
{
    /// <summary>
    /// A nested timeline's mixed audio as a content stream: rendered by its own
    /// TimelineAudioRenderer from the reference's in-point, ending where the
    /// reference's window ends.
    /// </summary>
    internal sealed class NestedTimelineContentAudio(TimelineReference reference, AudioSession session) : IContentAudio
    {
        private readonly TimelineAudioRenderer _renderer = new(reference.Timeline, session, isMaster: false);
        private long _position;

        public int Generation => 0;

        public bool Ready(bool wait) => true;

        public void Seek(long frame) => _position = Math.Max(0, frame);

        public int Read(Span<float> destination)
        {
            Timeline timeline = reference.Timeline;
            TimeSpan start = reference.Start ?? TimeSpan.Zero;
            TimeSpan length = reference.Duration ?? (timeline.Duration - start);

            long startFrame = session.FrameOf(start);
            long available = Math.Max(0, session.FrameOf(length) - _position);

            int channels = session.Format.Channels;
            int frames = (int)Math.Min(destination.Length / channels, available);

            for (int done = 0; done < frames;)
            {
                int block = Math.Min(session.BlockFrames, frames - done);
                _renderer.Render(startFrame + _position + done, destination.Slice(done * channels, block * channels));
                done += block;
            }

            _position += frames;
            return frames;
        }

        public void Dispose() => _renderer.Dispose();
    }
}
