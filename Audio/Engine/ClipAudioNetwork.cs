using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Audio.Processors;
using EditSharp.Components.Clips;
using EditSharp.Components.Nodes;
using EditSharp.Compositing.Graphs;

namespace EditSharp.Audio.Engine
{
    /// <summary>
    /// One audio clip's graph as live DSP. Each tick, source blocks are pushed
    /// through the nodes that feed the Output; a node runs once every input
    /// it's wired to has its block for the tick (unwired inputs are silence).
    /// A disabled node with one audio input and one audio output passes its
    /// input through.
    ///
    /// Processors belong to node objects and outlive rebuilds: when the
    /// graph's structure changes the network is rewired at the next tick, and
    /// nodes that are still there keep their state.
    /// </summary>
    internal sealed class ClipAudioNetwork(AudioClip clip, AudioSession session) : IDisposable
    {
        private sealed class Step(Node node, IAudioProcessor? processor, AudioPortBuffers ports)
        {
            public Node Node { get; } = node;
            public IAudioProcessor? Processor { get; } = processor;
            public AudioPortBuffers Ports { get; } = ports;
        }

        private readonly Dictionary<Node, IAudioProcessor?> _processors = new(ReferenceEqualityComparer.Instance);
        private readonly float[] _silence = new float[session.BlockFrames * session.Format.Channels];
        private List<Step> _steps = [];
        private float[]? _output;
        private int _shape;

        public AudioClip Clip { get; } = clip;

        /// <summary>Starts preparing the clip's sources (lookahead). True once all of them can be read.</summary>
        public bool Prepare(Graph graph)
        {
            Build(graph);
            bool ready = true;
            foreach (Step step in _steps)
                if (step.Processor is ContentInputProcessor input) ready &= input.Prepare();
            return ready;
        }

        /// <summary>Runs one tick and adds the clip's output into `mixInto` (frames * channels).</summary>
        public void Process(in AudioTick tick, Span<float> mixInto)
        {
            Build(tick.Graph);

            foreach (Step step in _steps)
            {
                Node node = step.Node;
                AudioPortBuffers ports = step.Ports;

                if (step.Processor is null)
                {
                    foreach (float[] output in ports.Outputs) Array.Clear(output, 0, tick.Samples);
                }
                else if (!node.Enabled && ports.Inputs.Length == 1 && ports.Outputs.Length == 1)
                {
                    Array.Copy(ports.Inputs[0], ports.Outputs[0], tick.Samples);
                }
                else
                {
                    step.Processor.Process(tick, ports);
                }

                if (ports.Outputs.Length > 0 && session.Taps.Has(node.Id))
                    session.Taps.Publish(node.Id, ports.Outputs[0], tick.Samples, tick.Format, session.TimeOf(tick.TimelineFrame));
            }

            if (_output is null) return;

            if (session.Taps.Has(tick.Graph.OutputNode.Id))
                session.Taps.Publish(tick.Graph.OutputNode.Id, _output, tick.Samples, tick.Format, session.TimeOf(tick.TimelineFrame));

            for (int i = 0; i < tick.Samples; i++) mixInto[i] += _output[i];
        }

        //rewire when the snapshot's structure differs from what's built
        private void Build(Graph graph)
        {
            int shape = Shape(graph);
            if (shape == _shape && _steps.Count > 0) return;
            _shape = shape;

            List<Node> order = GraphTopology.Order(graph);
            var buffers = new Dictionary<(Guid, string), float[]>();
            var steps = new List<Step>();
            int samples = session.BlockFrames * session.Format.Channels;

            foreach (Node node in order)
            {
                if (ReferenceEquals(node, graph.OutputNode)) continue;

                if (!_processors.TryGetValue(node, out IAudioProcessor? processor))
                    _processors[node] = processor = node.CreateAudioProcessor(session);

                float[][] inputs = [.. AudioPorts(node, PortDirection.Input).Select(port => Upstream(graph, node, port, buffers))];
                float[][] outputs = [.. AudioPorts(node, PortDirection.Output).Select(port => buffers[(node.Id, port)] = new float[samples])];

                steps.Add(new Step(node, processor, new AudioPortBuffers(inputs, outputs)));
            }

            _output = AudioPorts(graph.OutputNode, PortDirection.Input).Select(port => Upstream(graph, graph.OutputNode, port, buffers)).FirstOrDefault();
            if (ReferenceEquals(_output, _silence)) _output = null;

            //processors of nodes that left the graph go with them
            var present = new HashSet<Node>(order, ReferenceEqualityComparer.Instance);
            foreach (Node gone in _processors.Keys.Where(n => !present.Contains(n)).ToList())
            {
                _processors[gone]?.Dispose();
                _processors.Remove(gone);
            }

            _steps = steps;
        }

        private float[] Upstream(Graph graph, Node node, string port, Dictionary<(Guid, string), float[]> buffers)
        {
            Connection? c = graph.Connections.FirstOrDefault(x => x.ToNodeId == node.Id && x.ToPort == port);
            return c is not null && buffers.TryGetValue((c.FromNodeId, c.FromPort), out float[]? buffer) ? buffer : _silence;
        }

        private static IEnumerable<string> AudioPorts(Node node, PortDirection direction) =>
            node.Ports.Where(p => p.Type == PortType.Audio && p.Direction == direction).Select(p => p.Name);

        private static int Shape(Graph graph)
        {
            var hash = new HashCode();
            foreach (Node node in graph.Nodes) hash.Add(node.Id);
            foreach (Connection c in graph.Connections) hash.Add(HashCode.Combine(c.FromNodeId, c.FromPort, c.ToNodeId, c.ToPort));
            return hash.ToHashCode();
        }

        public void Dispose()
        {
            foreach (IAudioProcessor? processor in _processors.Values) processor?.Dispose();
            _processors.Clear();
        }
    }
}
