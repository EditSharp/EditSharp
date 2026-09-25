using System;
using EditSharp.Components;
using EditSharp.Components.Nodes;
using EditSharp.Compositing.Graphs;

namespace EditSharp.Audio.Engine
{
    /// <summary>
    /// Parameter curves for a block: values are evaluated at content time every
    /// StepFrames frames (and at the block's end) and interpolated between, so
    /// automation is smooth without an evaluation per sample.
    /// </summary>
    internal static class AudioAutomation
    {
        public const int StepFrames = 256;

        public static void Render(Animatable<float> parameter, in AudioTick tick, Span<float> values)
        {
            int frames = tick.Frames;
            float previous = parameter.Evaluate(tick.ContentStart);

            for (int start = 0; start < frames; start += StepFrames)
            {
                int end = Math.Min(start + StepFrames, frames);
                float next = parameter.Evaluate(tick.ContentTimeAt(end));

                for (int i = start; i < end; i++)
                    values[i] = previous + (next - previous) * ((i - start) / (float)(end - start));

                previous = next;
            }
        }

        /// <summary>
        /// Multiplies `values` by what's wired into `node`'s Value input `port`,
        /// if anything is. Returns false (values untouched) when nothing is.
        /// </summary>
        public static bool Modulate(Node node, string port, in AudioTick tick, Span<float> values)
        {
            Graph graph = tick.Graph;
            float? first = ValueGraphEvaluator.TryEvaluateConnectedInput(graph, node, port, tick.ContentStart);
            if (first is not { } previous) return false;

            int frames = tick.Frames;
            for (int start = 0; start < frames; start += StepFrames)
            {
                int end = Math.Min(start + StepFrames, frames);
                float next = ValueGraphEvaluator.TryEvaluateConnectedInput(graph, node, port, tick.ContentTimeAt(end)) ?? 1f;

                for (int i = start; i < end; i++)
                    values[i] *= previous + (next - previous) * ((i - start) / (float)(end - start));

                previous = next;
            }

            return true;
        }
    }
}
