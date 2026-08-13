using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components;
using EditSharp.Components.Clips;

namespace EditSharp.Assembly
{
    /// <summary>
    /// Mixes every clip's audio into the timeline's single audio stream.
    ///
    /// Audio ignores channel ORDER entirely — a channel's index decides what
    /// occludes what visually, and has no meaning for sound. What a channel does
    /// contribute is its Volume, applied once across everything on it, so the two
    /// levels compose: a clip at 0.5 on a channel at 0.5 plays at 0.25.
    ///
    /// Each clip's audio is delayed to its own Start rather than concatenated, so
    /// gaps between clips come out silent and clips on different channels overlap
    /// naturally.
    /// </summary>
    internal static class AudioMixer
    {
        public static string Compose(
            Timeline timeline,
            IReadOnlyDictionary<Clip, ClipContent> contents,
            InputGraph graph)
        {
            var channelLabels = new List<string>();

            foreach (Channel channel in timeline.Channels)
            {
                string? mixed = ComposeChannel(channel, contents, graph);

                if (mixed != null) channelLabels.Add(mixed);
            }

            double totalSeconds = timeline.Duration.TotalSeconds;

            //a timeline with no audio anywhere still needs a stream to map, or the
            //muxer has nothing to write and the run fails at the very end
            if (channelLabels.Count == 0) return BuildSilence(graph, totalSeconds);

            string merged = channelLabels.Count == 1
                ? channelLabels[0]
                : MixDown(channelLabels, graph);

            //pad out to the full timeline length so the audio doesn't end early on a
            //timeline whose tail is video only
            string padded = graph.NextLabel("amfinal");
            graph.FilterLines.Add(
                $"[{merged}]apad=whole_dur={GraphUtilities.Num(totalSeconds)}," +
                $"atrim=duration={GraphUtilities.Num(totalSeconds)},asetpts=PTS-STARTPTS[{padded}]");

            return padded;
        }

        private static string? ComposeChannel(
            Channel channel,
            IReadOnlyDictionary<Clip, ClipContent> contents,
            InputGraph graph)
        {
            var clipLabels = new List<string>();

            foreach (Clip clip in channel.Clips.Values.OrderBy(c => c.Start))
            {
                ClipContent content = contents[clip];
                if (content.AudioLabel == null) continue;

                //adelay takes milliseconds, per channel of the layout
                int delayMs = (int)Math.Round(clip.Start.TotalMilliseconds);

                string placed = graph.NextLabel("amclip");
                graph.FilterLines.Add(
                    $"[{content.AudioLabel}]adelay={delayMs}|{delayMs}[{placed}]");

                clipLabels.Add(placed);
            }

            if (clipLabels.Count == 0) return null;

            string mixed = clipLabels.Count == 1 ? clipLabels[0] : MixDown(clipLabels, graph);

            if (Math.Abs(channel.Volume - 1f) < 0.0001f) return mixed;

            string levelled = graph.NextLabel("amchvol");
            graph.FilterLines.Add(
                $"[{mixed}]volume={GraphUtilities.Num(channel.Volume)}[{levelled}]");

            return levelled;
        }

        /// <summary>
        /// Sums several audio streams.
        ///
        /// normalize=0 because amix otherwise divides by the input count, so adding
        /// a quiet channel would silently duck everything else. dropout_transition=0
        /// stops it ramping the remaining inputs up whenever one of them ends, which
        /// on a timeline of short clips would be constant and audible.
        /// duration=longest so the mix runs until the last clip finishes.
        /// </summary>
        private static string MixDown(List<string> labels, InputGraph graph)
        {
            string mixed = graph.NextLabel("ammix");
            graph.FilterLines.Add(
                $"{string.Join("", labels.Select(l => $"[{l}]"))}" +
                $"amix=inputs={labels.Count}:duration=longest:" +
                $"dropout_transition=0:normalize=0[{mixed}]");

            return mixed;
        }

        private static string BuildSilence(InputGraph graph, double totalSeconds)
        {
            string silence = graph.NextLabel("amsilence");
            graph.FilterLines.Add(
                $"anullsrc=channel_layout=stereo:sample_rate=44100," +
                $"atrim=duration={GraphUtilities.Num(totalSeconds)}," +
                $"asetpts=PTS-STARTPTS[{silence}]");

            return silence;
        }
    }
}
