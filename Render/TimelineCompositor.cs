using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Components.Clips;

namespace EditSharp.Render
{
    /// <summary>
    /// Stacks the timeline's channels into a single finished video stream.
    ///
    /// Channels composite in list order, index 0 first, each drawn on top of the
    /// result so far. Every channel shares ONE accumulator rather than being
    /// rendered into its own full-length canvas — a channel only ever costs the
    /// duration of the clips actually on it.
    ///
    /// The accumulator starts fully transparent so that gaps anywhere in the stack
    /// stay transparent, and is flattened onto opaque black at the very end. That
    /// keeps "what shows through a gap" a single decision made in one place, rather
    /// than something the bottom channel has to be special-cased for.
    /// </summary>
    internal static class TimelineCompositor
    {
        public static string Compose(
            Timeline timeline,
            IReadOnlyDictionary<Clip, ClipContent> contents,
            InputGraph graph, int canvasWidth, int canvasHeight, int fps,
            ConcurrentBag<string> tempFiles)
        {
            double totalSeconds = timeline.Duration.TotalSeconds;

            if (totalSeconds <= 0)
                throw new InvalidOperationException(
                    "Timeline has no duration — every channel is empty.");

            string accumulator = GraphUtilities.BuildTransparentBlank(
                graph, canvasWidth, canvasHeight, fps, totalSeconds, "tlbase");

            //settb so that any later xfade between segments sees a consistent
            //timebase; source files can carry unusual native ones
            string based = graph.NextLabel("tlbasetb");
            graph.FilterLines.Add($"[{accumulator}]settb=AVTB[{based}]");
            accumulator = based;

            foreach (Channel channel in timeline.Channels)
            {
                accumulator = ChannelCompositor.Compose(
                    channel, accumulator, contents, graph,
                    canvasWidth, canvasHeight, fps, tempFiles);
            }

            return Flatten(accumulator, graph, canvasWidth, canvasHeight, fps, totalSeconds);
        }

        /// <summary>
        /// Drops the composite onto opaque black and converts to yuv420p. The final
        /// output cannot itself carry transparency, so anything still uncovered
        /// resolves to black here.
        /// </summary>
        private static string Flatten(
            string accumulator, InputGraph graph,
            int canvasWidth, int canvasHeight, int fps, double totalSeconds)
        {
            string backdrop = graph.NextLabel("tlflatbg");
            graph.FilterLines.Add(
                $"color=black:size={canvasWidth}x{canvasHeight}:rate={fps}:" +
                $"duration={GraphUtilities.Num(totalSeconds)},format=rgba,settb=AVTB[{backdrop}]");

            string flattened = graph.NextLabel("tlvideo");
            graph.FilterLines.Add(
                $"[{backdrop}][{accumulator}]overlay=0:0:format=auto:shortest=0," +
                $"format=yuv420p[{flattened}]");

            return flattened;
        }
    }
}
