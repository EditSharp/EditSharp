using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EditSharp.Components;
using EditSharp.Components.Clips;
using EditSharp.Components.Nodes;
using EditSharp.Components.Nodes.Sources.Audio;

namespace EditSharp.Composite
{
    /// <summary>
    /// Mixes every clip's audio into the timeline's single master AudioBuffer.
    ///
    /// "CLIPS ARE GRAPHS" REWRITE: the old AudibleClip/AudioClip.Source/
    /// TimelineAudioClip shapes are gone. AudioClip is now the only concrete
    /// audible Clip type, and what used to distinguish "a real audio file"
    /// from "an embedded nested timeline" is now just which InputNode(s) its
    /// single Graph happens to contain — MediaAudioSourceNode,
    /// ToneGeneratorInputNode (brand new — there was no synthesized-tone clip
    /// type before this), or TimelineAudioInputNode. ComposeClipAsync
    /// resolves EVERY InputNode in a clip's graph to its own raw AudioBuffer
    /// (dispatching on InputNode type, keyed by node Id), fits each to the
    /// clip's own Duration, then hands the whole resolved-inputs dictionary
    /// to AudioEffectGraphEvaluator for genuine per-node graph evaluation —
    /// GainNode/EQNode/CompressorNode/AudioMixNode all really run now, not
    /// just a single default GainNode's static value.
    ///
    /// ONLY AudioChannels ARE EVEN CONSIDERED HERE, UNCONDITIONALLY — not a
    /// "known gap": a VideoChannel's clips are all VideoClip, and per the
    /// schema a VideoClip NEVER represents or contributes audio to a render,
    /// full stop. A VideoClip's MediaSourceNode.Source may well have its own
    /// audio track in the underlying file, but that track is never read,
    /// never mixed, and never surfaced anywhere in this pipeline. Getting a
    /// video file's sound onto the timeline means pairing a VideoClip with an
    /// AudioClip on the SAME Source — see LinkGroup.CreateAudioVideoPair,
    /// which already exists specifically for this "drag a video with audio
    /// onto the timeline" case.
    ///
    /// A channel's index still decides what occludes what visually, and still
    /// has no meaning for sound — audio ignores channel order entirely.
    ///
    /// REWRITE ("channels split by kind"): iterates timeline.AudioChannels
    /// directly now instead of timeline.Channels filtered by `is not
    /// AudioChannel` — Timeline keeps VideoChannel and AudioChannel as two
    /// separate lists (see Timeline.cs's own remarks), so there's no longer
    /// a mixed list to filter here.
    /// </summary>
    internal static class AudioMixer
    {
        /// <summary>
        /// Builds the mixed master AudioBuffer for the WHOLE timeline, from
        /// t=0 to timeline.Duration, at the requested sample rate/channel
        /// count. Recursive: a TimelineAudioInputNode embedding another
        /// Timeline calls back into this same method for that nested timeline.
        /// </summary>
        public static async Task<AudioBuffer> ComposeAsync(
            Timeline timeline, int sampleRate, int channels, CancellationToken token = default)
        {
            int totalFrames = AudioBuffer.FramesForDuration(timeline.Duration, sampleRate);
            var master = AudioBuffer.Silence(sampleRate, channels, totalFrames);

            foreach (AudioChannel audioChannel in timeline.AudioChannels)
            {
                AudioBuffer channelBuffer = AudioBuffer.Silence(sampleRate, channels, totalFrames);

                foreach (Clip clip in audioChannel.Clips)
                {
                    //AudioChannel.IsValidClipType already enforces this at
                    //placement time — this is belt-and-suspenders, not a
                    //real fallback path.
                    if (clip is not AudioClip audio) continue;

                    AudioBuffer evaluated = await ComposeClipAsync(audio, sampleRate, channels, token);

                    int startFrame = AudioBuffer.FramesForDuration(clip.Start, sampleRate);
                    channelBuffer.MixFrom(evaluated, startFrame);
                }

                channelBuffer.ApplyGain(audioChannel.Volume);
                master.MixFrom(channelBuffer, 0);
            }

            return master;
        }

        /// <summary>
        /// Resolves every InputNode in `clip`'s graph to its own raw
        /// AudioBuffer (already fitted to exactly `clip.Duration`), then runs
        /// the clip's whole graph for real via AudioEffectGraphEvaluator.
        /// </summary>
        private static async Task<AudioBuffer> ComposeClipAsync(
            AudioClip clip, int sampleRate, int channels, CancellationToken token)
        {
            var resolvedInputs = new Dictionary<Guid, AudioBuffer>();

            foreach (InputNode node in clip.Graph.Nodes.OfType<InputNode>())
            {
                resolvedInputs[node.Id] = node switch
                {
                    AudioSourceNode media =>
                        await PcmAudioDecoder.DecodeAsync(media.Source, clip.Duration, sampleRate, channels, token),

                    ToneGeneratorInputNode tone =>
                        SynthesizeTone(tone, clip.Duration, sampleRate, channels),

                    TimelineAudioInputNode embed =>
                        await ResolveNestedTimelineAudioAsync(embed.Reference, clip.Duration, sampleRate, channels, token),

                    _ => throw new NotSupportedException(
                        $"AudioMixer has no dispatch for {node.GetType().Name}."),
                };
            }

            return AudioEffectGraphEvaluator.Evaluate(clip.Graph, resolvedInputs);
        }

        /// <summary>
        /// A synthesized tone — brand new content type, the audio-domain
        /// equivalent of ColorGeneratorInputNode on the video side. Frequency/
        /// Amplitude are evaluated once per sample (unlike the effect-graph
        /// evaluator's own once-per-automation-block cadence — a tone's pitch
        /// is exactly what a listener is meant to hear move, so it gets full
        /// per-sample precision here) via continuous phase accumulation, so a
        /// changing Frequency curve doesn't introduce phase discontinuities.
        /// </summary>
        private static AudioBuffer SynthesizeTone(
            ToneGeneratorInputNode tone, TimeSpan duration, int sampleRate, int channels)
        {
            int frameCount = AudioBuffer.FramesForDuration(duration, sampleRate);
            var samples = new float[frameCount * channels];

            const double twoPi = 2.0 * Math.PI;
            double phase = 0.0;

            for (int frame = 0; frame < frameCount; frame++)
            {
                TimeSpan t = TimeSpan.FromSeconds(frame / (double)sampleRate);
                float frequency = tone.Frequency.Evaluate(t);
                float amplitude = tone.Amplitude.Evaluate(t);

                double value = tone.Waveform switch
                {
                    Waveform.Sine => Math.Sin(phase),
                    Waveform.Square => Math.Sin(phase) >= 0 ? 1.0 : -1.0,
                    Waveform.Sawtooth => 2.0 * ((phase / twoPi) - Math.Floor((phase / twoPi) + 0.5)),
                    Waveform.Triangle => (2.0 / Math.PI) * Math.Asin(Math.Sin(phase)),
                    _ => throw new NotSupportedException($"Unknown Waveform: {tone.Waveform}"),
                };

                float sample = (float)(value * amplitude);
                int baseIdx = frame * channels;
                for (int ch = 0; ch < channels; ch++) samples[baseIdx + ch] = sample;

                phase += twoPi * frequency / sampleRate;
                if (phase > twoPi) phase %= twoPi; //keep the accumulator bounded over a long clip
            }

            return new AudioBuffer(sampleRate, channels, samples);
        }

        /// <summary>
        /// Gets one TimelineAudioInputNode's own raw (pre-Graph) audio:
        /// recursively mixes its embedded Timeline and windows the result per
        /// its TimelineReference, then fits to the owning clip's Duration.
        /// </summary>
        private static async Task<AudioBuffer> ResolveNestedTimelineAudioAsync(
            TimelineReference reference, TimeSpan clipDuration, int sampleRate, int channels, CancellationToken token)
        {
            Timeline nested = reference.Timeline;

            AudioBuffer nestedMaster = await ComposeAsync(nested, sampleRate, channels, token);

            TimeSpan refStart = reference.Start ?? TimeSpan.Zero;
            int startFrame = AudioBuffer.FramesForDuration(refStart, sampleRate);

            TimeSpan refDuration = reference.Duration ?? (nested.Duration - refStart);
            if (refDuration < TimeSpan.Zero) refDuration = TimeSpan.Zero;
            int refFrames = AudioBuffer.FramesForDuration(refDuration, sampleRate);

            AudioBuffer window = nestedMaster.Slice(startFrame, refFrames);

            int targetFrames = AudioBuffer.FramesForDuration(clipDuration, sampleRate);

            //A nested timeline has no ffmpeg -stream_loop equivalent — it's
            //already a fixed, fully-composed in-memory buffer by the time it
            //gets here — so holding the last frame (never looping) is the
            //correct fit behaviour regardless of whether the reference had an
            //explicit Start/Duration.
            return window.FitToFrames(targetFrames, allowLoop: false);
        }
    }
}