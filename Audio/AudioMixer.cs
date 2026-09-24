using System;
using System.Threading;
using System.Threading.Tasks;
using EditSharp.Audio.Engine;
using EditSharp.Components;
using EditSharp.Rendering;

namespace EditSharp.Audio
{
    /// <summary>
    /// A whole timeline's mixed audio in one buffer, rendered block by block
    /// through the streaming engine (TimelineAudioRenderer). Sources are
    /// waited for, as in an export; failures are silence and go to `report`.
    /// </summary>
    internal static class AudioMixer
    {
        public static Task<AudioBuffer> ComposeAsync(
            Timeline timeline, int sampleRate, int channels, CancellationToken token = default, RenderReportBuilder? report = null) => Task.Run(() =>
        {
            var session = new AudioSession(new AudioFormat(sampleRate, channels), waitForSources: true, report);
            using var renderer = new TimelineAudioRenderer(timeline, session);

            int totalFrames = AudioBuffer.FramesForDuration(timeline.Duration, sampleRate);
            var samples = new float[totalFrames * channels];

            for (int frame = 0; frame < totalFrames; frame += session.BlockFrames)
            {
                token.ThrowIfCancellationRequested();
                int count = Math.Min(session.BlockFrames, totalFrames - frame);
                renderer.Render(frame, samples.AsSpan(frame * channels, count * channels));
            }

            return new AudioBuffer(sampleRate, channels, samples);
        }, token);
    }
}
