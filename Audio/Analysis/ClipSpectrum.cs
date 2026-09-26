using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components.Clips;
using EditSharp.Components.Media;
using EditSharp.Components.Nodes;
using EditSharp.Components.Nodes.Input;
using EditSharp.Compositing.Graphs;
using EditSharp.History;

namespace EditSharp.Audio.Analysis
{
    /// <summary>A clip's processed audio, frame by frame in the frequency domain, after its graph's nodes have described their effect.</summary>
    /// <param name="ContentStart">The content time of the first frame, at 1x; negative frames lie before the in-point.</param>
    /// <param name="FrameSeconds">How long each frame is.</param>
    /// <param name="Bands">Band energies, frame-major: frame f's <see cref="AudioAnalysis.BandCount"/> bands start at f * BandCount.</param>
    /// <param name="Peak">Each frame's peak, 0 to 1 for unclipped audio.</param>
    public sealed record SpectralEnvelope(Time ContentStart, double FrameSeconds, float[] Bands, float[] Peak)
    {
        /// <summary>How many frames there are.</summary>
        public int Count => Peak.Length;

        /// <summary>One frame's bands.</summary>
        /// <param name="frame">The frame index.</param>
        /// <returns>The energies.</returns>
        public ReadOnlySpan<float> BandsOf(int frame) => Bands.AsSpan(frame * AudioAnalysis.BandCount, AudioAnalysis.BandCount);

        /// <summary>One frame's RMS level, from its energy.</summary>
        /// <param name="frame">The frame index.</param>
        /// <returns>The level.</returns>
        public float RmsOf(int frame)
        {
            float sum = 0f;
            foreach (float band in BandsOf(frame)) sum += band;
            return MathF.Sqrt(Math.Max(0f, sum));
        }
    }

    /// <summary>Folds an audio clip's graph over its sources' analyses, frame by frame, in the frequency domain.</summary>
    /// <remarks>
    /// Every node describes its effect through <see cref="Node.DescribeSpectrum"/>; a disabled node passes its first input through.
    /// Media inputs read <see cref="AudioAnalysisCache"/>, so a clip whose media has no analysis yet can't be evaluated until it's built.
    /// </remarks>
    public static class ClipSpectrum
    {
        /// <summary>Whether every media the clip reads has its analysis in memory.</summary>
        /// <param name="clip">The clip.</param>
        /// <returns>True when <see cref="Evaluate"/> can run.</returns>
        public static bool IsReady(AudioClip clip)
        {
            ArgumentNullException.ThrowIfNull(clip);
            return MissingMedia(clip).Count == 0;
        }

        /// <summary>The media files the clip reads whose analysis is not in memory yet.</summary>
        /// <param name="clip">The clip.</param>
        /// <returns>Their paths, without repeats.</returns>
        public static IReadOnlyList<string> MissingMedia(AudioClip clip)
        {
            ArgumentNullException.ThrowIfNull(clip);

            var missing = new List<string>();
            foreach (AudioMediaNode node in clip.Graph.AllNodes.OfType<AudioMediaNode>())
            {
                string? path = node.Media?.Path;
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) continue;
                if (!AudioAnalysisCache.TryGet(path, out _) && !missing.Contains(path, StringComparer.OrdinalIgnoreCase)) missing.Add(path);
            }

            return missing;
        }

        /// <summary>The clip's processed envelope over a stretch of its content.</summary>
        /// <param name="clip">The clip.</param>
        /// <param name="contentStart">The content time of the first frame; negative reaches before the in-point.</param>
        /// <param name="frameCount">How many frames to evaluate.</param>
        /// <returns>The envelope, or null when a media the clip reads has no analysis in memory (see <see cref="AudioAnalysisCache.GetAsync"/>).</returns>
        /// <exception cref="ArgumentNullException"><paramref name="clip"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="frameCount"/> is negative.</exception>
        public static SpectralEnvelope? Evaluate(AudioClip clip, Time contentStart, int frameCount)
        {
            ArgumentNullException.ThrowIfNull(clip);
            if (frameCount < 0) throw new ArgumentOutOfRangeException(nameof(frameCount), "frameCount can't be negative.");
            if (!IsReady(clip)) return null;

            Graph graph;
            using (ModelLock.Read()) graph = clip.Graph.Snapshot();

            List<Node> order = GraphTopology.Order(graph);
            var outputs = new Dictionary<Guid, SpectralFrame>();
            var states = new object?[order.Count];
            var silence = new SpectralFrame();
            var inputs = new List<SpectralFrame>();

            foreach (Node node in order) outputs[node.Id] = new SpectralFrame();

            float[] peak = new float[frameCount];
            float[] bands = new float[frameCount * AudioAnalysis.BandCount];

            for (int f = 0; f < frameCount; f++)
            {
                var context = new SpectralContext(contentStart + AudioAnalysis.FrameTime(f), AudioAnalysis.FrameSeconds);

                for (int n = 0; n < order.Count; n++)
                {
                    Node node = order[n];
                    if (ReferenceEquals(node, graph.OutputNode)) continue;

                    inputs.Clear();
                    foreach (NodePort port in node.Ports)
                    {
                        if (port.Type != PortType.Audio || port.Direction != PortDirection.Input) continue;
                        inputs.Add(Upstream(graph, node, port.Name, outputs) ?? silence);
                    }

                    SpectralFrame output = outputs[node.Id];

                    if (!node.Enabled)
                    {
                        if (inputs.Count > 0) output.CopyFrom(inputs[0]);
                        else output.Clear();
                        continue;
                    }

                    node.DescribeSpectrum(context, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(inputs), output, ref states[n]);
                }

                SpectralFrame? final = graph.OutputNode.Ports
                    .Where(p => p.Type == PortType.Audio && p.Direction == PortDirection.Input)
                    .Select(p => Upstream(graph, graph.OutputNode, p.Name, outputs))
                    .FirstOrDefault(frame => frame is not null);

                peak[f] = final?.Peak ?? 0f;
                if (final is not null) final.Bands.AsSpan().CopyTo(bands.AsSpan(f * AudioAnalysis.BandCount, AudioAnalysis.BandCount));
            }

            return new SpectralEnvelope(contentStart, AudioAnalysis.FrameSeconds, bands, peak);
        }

        private static SpectralFrame? Upstream(Graph graph, Node node, string port, Dictionary<Guid, SpectralFrame> outputs)
        {
            Connection? c = graph.Connections.FirstOrDefault(x => x.ToNodeId == node.Id && x.ToPort == port);
            return c is not null && outputs.TryGetValue(c.FromNodeId, out SpectralFrame? frame) ? frame : null;
        }
    }
}
