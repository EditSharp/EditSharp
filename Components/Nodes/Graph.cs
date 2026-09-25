using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.History;

namespace EditSharp.Components.Nodes
{
    /// <summary>What a port carries; only ports of the same type connect.</summary>
    public enum PortType
    {
        /// <summary>An image.</summary>
        Image,

        /// <summary>A mask: an image whose alpha says how strongly an effect applies.</summary>
        Mask,

        /// <summary>Audio samples.</summary>
        Audio,

        /// <summary>A single number, evaluated at each moment.</summary>
        Value,
    }

    /// <summary>Whether a port takes a connection in or sends one out.</summary>
    public enum PortDirection
    {
        /// <summary>Takes at most one connection in.</summary>
        Input,

        /// <summary>Sends to any number of inputs.</summary>
        Output,
    }

    /// <summary>A port a node's type declares.</summary>
    public sealed class NodePort
    {
        /// <summary>The port's name, unique among the node's ports of the same direction; <see cref="Graph.Connect"/> takes it.</summary>
        public string Name { get; }

        /// <summary>What the port carries.</summary>
        public PortType Type { get; }

        /// <summary>Whether it's an input or an output.</summary>
        public PortDirection Direction { get; }

        /// <summary>Whether the node does its job with this input unconnected, such as a mask or a modulation input.</summary>
        public bool Optional { get; }

        /// <summary>Declares a port.</summary>
        /// <param name="name">The port's name.</param>
        /// <param name="type">What it carries.</param>
        /// <param name="direction">Whether it's an input or an output.</param>
        /// <param name="optional">Whether the node works with it unconnected.</param>
        public NodePort(string name, PortType type, PortDirection direction, bool optional = false)
        {
            Name = name;
            Type = type;
            Direction = direction;
            Optional = optional;
        }
    }

    /// <summary>A wire from one node's output port to another's input port.</summary>
    public sealed class Connection
    {
        /// <summary>The <see cref="Node.Id"/> of the node the wire comes from.</summary>
        public Guid FromNodeId { get; }

        /// <summary>The output port it comes from.</summary>
        public string FromPort { get; }

        /// <summary>The <see cref="Node.Id"/> of the node the wire goes to.</summary>
        public Guid ToNodeId { get; }

        /// <summary>The input port it goes to.</summary>
        public string ToPort { get; }

        internal Connection(Guid fromNodeId, string fromPort, Guid toNodeId, string toPort)
        {
            FromNodeId = fromNodeId;
            FromPort = fromPort;
            ToNodeId = toNodeId;
            ToPort = toPort;
        }
    }

    /// <summary>Which kind of signal a graph carries; a node with ports of the other kind can't be added.</summary>
    public enum NodeDomain
    {
        /// <summary>A video clip's graph: Image, Mask and Value ports.</summary>
        Image,

        /// <summary>An audio clip's graph: Audio and Value ports.</summary>
        Audio,
    }

    /// <summary>A clip's content and effects: nodes, wired from inputs through effects to one output node.</summary>
    /// <remarks>
    /// Nodes and connections can only be changed through <see cref="AddNode"/>,
    /// <see cref="RemoveNode"/>, <see cref="Connect"/> and <see cref="Disconnect"/>,
    /// so a graph never has a wire to a missing node, a cycle, or mismatched port
    /// types. Each change is recorded in the current History transaction. An input
    /// left unconnected is allowed while editing: an unconnected image input is
    /// transparent, and an unconnected audio input is silent.
    /// </remarks>
    public sealed class Graph
    {
        /// <summary>Which kind of signal the graph carries.</summary>
        public NodeDomain Domain { get; }

        //what holds this graph: a clip, or a composite node around it
        internal Clips.Clip? Clip { get; set; }
        internal CompositeNode? Composite { get; set; }

        //the clip this graph is in, through any composites around it
        internal Clips.Clip? OwnerClip
        {
            get
            {
                for (Graph? graph = this; graph is not null; graph = graph.Composite?.Graph)
                    if (graph.Clip is { } clip) return clip;
                return null;
            }
        }

        private readonly List<Node> _nodes = [];
        private readonly List<Connection> _connections = [];

        /// <summary>The nodes directly in this graph, the output node included; a composite's inner nodes aren't listed.</summary>
        public IReadOnlyList<Node> Nodes => _nodes;

        /// <summary>Every keyframeable value on every node; see <see cref="Node.Animatables"/>.</summary>
        public IEnumerable<IAnimatable> Animatables => _nodes.SelectMany(n => n.Animatables);

        /// <summary>The wires between this graph's nodes.</summary>
        public IReadOnlyList<Connection> Connections => _connections;

        /// <summary>Every node, including those inside composites, except the composites' own output nodes.</summary>
        /// <remarks>Use it to find everything a clip really contains, such as its sources.</remarks>
        public IEnumerable<Node> AllNodes
            => _nodes.Any(n => n is CompositeNode)
                ? _nodes.SelectMany(n => n is CompositeNode c ? c.Inner.AllNodes.Where(x => x is not global::EditSharp.Components.Nodes.OutputNode) : Enumerable.Repeat(n, 1))
                : _nodes;

        /// <summary>The graph with every <see cref="CompositeNode"/> replaced by the nodes inside it, rewired to the inner ports its wires really go to.</summary>
        /// <remarks>The inner nodes are the same objects, not copies. A graph with no composites returns itself.</remarks>
        public Graph Flattened => _nodes.Any(n => n is CompositeNode) ? Flatten() : this;

        //the flattened structure with its own node and connection lists (the nodes are shared), so it can be
        //walked while the live graph is edited; take it under ModelLock's read side
        internal Graph Snapshot()
        {
            Graph flat = Flattened;
            return new Graph(flat.Domain, flat.OutputNode, [.. flat._nodes], [.. flat._connections]);
        }

        private Graph Flatten()
        {
            List<Node> nodes = [];
            List<Connection> connections = [];

            //where a composite's exposed input really goes, what feeds its
            //output, and which input a disabled one passes straight through
            var inputTargets = new Dictionary<(Guid, string), (Guid, string)>();
            var outputSources = new Dictionary<Guid, (Guid, string)?>();
            var passThrough = new Dictionary<Guid, string>();

            foreach (Node node in _nodes)
            {
                if (node is not CompositeNode composite)
                {
                    nodes.Add(node);
                    continue;
                }

                Graph inner = composite.Inner.Flattened;

                foreach (Node n in inner.Nodes)
                    if (!ReferenceEquals(n, inner.OutputNode)) nodes.Add(n);

                foreach (Connection c in inner.Connections)
                    if (c.ToNodeId != inner.OutputNode.Id) connections.Add(c);

                foreach (ExposedPort p in composite.Inputs)
                    inputTargets[(composite.Id, p.Name)] = (p.Node, p.Port);

                if (!composite.Enabled)
                {
                    //bypass: the output is whatever came in on the first
                    //exposed input of the output's own type, if any
                    PortType outputType = composite.Output.Type;
                    NodePort? through = composite.Ports.FirstOrDefault(p => p.Direction == PortDirection.Input && p.Type == outputType);

                    outputSources[composite.Id] = null;
                    if (through is not null) passThrough[composite.Id] = through.Name;
                    continue;
                }

                Connection? feed = inner.Connections.FirstOrDefault(c => c.ToNodeId == inner.OutputNode.Id);
                outputSources[composite.Id] = feed is null ? null : (feed.FromNodeId, feed.FromPort);
            }

            foreach (Connection c in _connections)
            {
                Guid fromNode = c.FromNodeId;
                string fromPort = c.FromPort;

                if (outputSources.TryGetValue(c.FromNodeId, out (Guid, string)? source))
                {
                    if (source is null)
                    {
                        //a disabled composite hands on whatever fed its pass-through input
                        if (!passThrough.TryGetValue(c.FromNodeId, out string? via)) continue;

                        Connection? fed = _connections.FirstOrDefault(x => x.ToNodeId == c.FromNodeId && x.ToPort == via);
                        if (fed is null) continue;

                        (fromNode, fromPort) = (fed.FromNodeId, fed.FromPort);
                    }
                    else (fromNode, fromPort) = source.Value;
                }

                Guid toNode = c.ToNodeId;
                string toPort = c.ToPort;

                if (inputTargets.TryGetValue((c.ToNodeId, c.ToPort), out (Guid, string) target)) (toNode, toPort) = target;

                connections.Add(new Connection(fromNode, fromPort, toNode, toPort));
            }

            return new Graph(Domain, OutputNode, nodes, connections);
        }

        /// <summary>Where the graph's result leaves it; it can't be removed.</summary>
        public OutputNode OutputNode { get; }

        /// <summary>The input nodes directly in this graph, as a new list.</summary>
        public IReadOnlyList<InputNode> InputNodes => _nodes.OfType<InputNode>().ToList();

        private Graph(NodeDomain domain, OutputNode outputNode)
        {
            Domain = domain;
            OutputNode = outputNode;
            outputNode.Graph = this;
            _nodes.Add(outputNode);
        }

        //a flattened view - see Flattened. shares node objects with its source, owns nothing
        private Graph(NodeDomain domain, OutputNode outputNode, List<Node> nodes, List<Connection> connections)
        {
            Domain = domain;
            OutputNode = outputNode;
            _nodes = nodes;
            _connections = connections;
        }

        /// <summary>A video graph with the usual chain: the input, then a <see cref="Effects.TintNode"/>, then a <see cref="Effects.TransformNode"/>, then the output.</summary>
        /// <remarks>Nothing is recorded in history.</remarks>
        /// <param name="input">The node the content comes from.</param>
        /// <returns>The graph.</returns>
        public static Graph CreateVideoGraph(InputNode input)
        {
            using var _ = Transaction.Suppress();

            var graph = new Graph(NodeDomain.Image, new ImageOutputNode());

            graph.AddNode(input);
            var tint = (Effects.TintNode)graph.AddNode(new Effects.TintNode());
            var transform = (Effects.TransformNode)graph.AddNode(new Effects.TransformNode());

            graph.Connect(input.Id, "Image", tint.Id, "Image");
            graph.Connect(tint.Id, "Image", transform.Id, "Image");
            graph.Connect(transform.Id, "Image", graph.OutputNode.Id, "Image");

            return graph;
        }

        /// <summary>An audio graph with the usual chain: the input, then a <see cref="Effects.GainNode"/>, then the output.</summary>
        /// <remarks>Nothing is recorded in history.</remarks>
        /// <param name="input">The node the content comes from.</param>
        /// <returns>The graph.</returns>
        public static Graph CreateAudioGraph(InputNode input)
        {
            using var _ = Transaction.Suppress();

            var graph = new Graph(NodeDomain.Audio, new AudioOutputNode());

            graph.AddNode(input);
            var gain = (Effects.GainNode)graph.AddNode(new Effects.GainNode());

            graph.Connect(input.Id, "Audio", gain.Id, "Audio");
            graph.Connect(gain.Id, "Audio", graph.OutputNode.Id, "Audio");

            return graph;
        }

        /// <summary>A video graph with only its output node, for building a graph from scratch.</summary>
        /// <returns>The graph.</returns>
        public static Graph CreateEmptyVideoGraph() => new(NodeDomain.Image, new ImageOutputNode());

        /// <summary>An audio graph with only its output node, for building a graph from scratch.</summary>
        /// <returns>The graph.</returns>
        public static Graph CreateEmptyAudioGraph() => new(NodeDomain.Audio, new AudioOutputNode());

        /// <summary>A deep copy, with new node ids and the connections rewired to match.</summary>
        /// <remarks>Nothing is recorded in history.</remarks>
        /// <returns>The copy.</returns>
        public Graph Duplicate() => Duplicate(out _);

        //a deep copy that also reports which new node id each old one became, so a composite can carry its exposures across
        internal Graph Duplicate(out Dictionary<Guid, Guid> idMap)
        {
            using var _ = Transaction.Suppress();

            var map = new Dictionary<Guid, Node>();
            var ids = new Dictionary<Guid, Guid>();

            Node NewOf(Node original)
            {
                Node copy = original.Duplicate();
                map[original.Id] = copy;
                ids[original.Id] = copy.Id;
                return copy;
            }

            var newOutput = (OutputNode)NewOf(OutputNode);
            var graph = new Graph(Domain, newOutput);

            foreach (Node node in _nodes)
            {
                if (ReferenceEquals(node, OutputNode)) continue;

                Node copy = NewOf(node);
                copy.Graph = graph;
                graph._nodes.Add(copy);
            }

            foreach (Connection c in _connections)
            {
                graph._connections.Add(new Connection(
                    map[c.FromNodeId].Id, c.FromPort, map[c.ToNodeId].Id, c.ToPort));
            }

            idMap = ids;
            return graph;
        }

        private Node? Find(Guid id) => _nodes.FirstOrDefault(n => n.Id == id);

        //from the node's ports: an Audio port makes it Audio, an Image or Mask port Image, and only Value ports
        //(or none) null, so it goes in either
        private static NodeDomain? InferDomain(Node node)
        {
            bool hasAudio = node.Ports.Any(p => p.Type == PortType.Audio);
            bool hasImage = node.Ports.Any(p => p.Type is PortType.Image or PortType.Mask);

            if (hasAudio && hasImage)
                throw new InvalidOperationException(
                    $"{node.GetType().Name} declares both Audio and Image/Mask ports; a node carries one kind of " +
                    "signal, or only Value ports.");

            if (hasAudio) return NodeDomain.Audio;
            if (hasImage) return NodeDomain.Image;
            return null; //universal
        }

        /// <summary>Adds a node, unconnected.</summary>
        /// <param name="node">The node to add; a node with only Value ports goes in either kind of graph.</param>
        /// <returns><paramref name="node"/>.</returns>
        /// <exception cref="InvalidOperationException">The node's ports are for the other kind of graph, or for both.</exception>
        public Node AddNode(Node node)
        {
            NodeDomain? nodeDomain = InferDomain(node);

            if (nodeDomain != null && nodeDomain != Domain)
                throw new InvalidOperationException(
                    $"Cannot add a {nodeDomain} node to a {Domain} Graph.");

            Timeline.Reembed(OwnerClip, [], Timeline.EmbeddedIn(node));

            node.Graph = this;
            Transaction.Apply(() => _nodes.Add(node), () => _nodes.Remove(node), "add node");
            return node;
        }

        /// <summary>Removes a node and every connection to or from it.</summary>
        /// <param name="node">The node to remove.</param>
        /// <exception cref="InvalidOperationException"><paramref name="node"/> is the <see cref="OutputNode"/>.</exception>
        public void RemoveNode(Node node)
        {
            if (ReferenceEquals(node, OutputNode))
                throw new InvalidOperationException("OutputNode cannot be removed from a Graph.");

            int index = _nodes.IndexOf(node);
            if (index >= 0) Timeline.Reembed(OwnerClip, Timeline.EmbeddedIn(node), []);

            List<Connection> severed = _connections.Where(c => c.FromNodeId == node.Id || c.ToNodeId == node.Id).ToList();

            Transaction.Apply(
                () => { _nodes.Remove(node); _connections.RemoveAll(severed.Contains); },
                () => { _nodes.Insert(System.Math.Min(index, _nodes.Count), node); _connections.AddRange(severed); },
                "remove node");
        }

        /// <summary>Wires one node's output port to another's input port.</summary>
        /// <param name="fromNode">The <see cref="Node.Id"/> of the node the wire comes from.</param>
        /// <param name="fromPort">The name of its output port.</param>
        /// <param name="toNode">The <see cref="Node.Id"/> of the node the wire goes to.</param>
        /// <param name="toPort">The name of its input port.</param>
        /// <returns>The new connection.</returns>
        /// <exception cref="ArgumentException">Either node isn't in this graph, or has no such port.</exception>
        /// <exception cref="InvalidOperationException">The ports' types differ, the input is already connected (disconnect it first), or the wire would make a cycle.</exception>
        public Connection Connect(Guid fromNode, string fromPort, Guid toNode, string toPort)
        {
            Node from = Find(fromNode) ?? throw new ArgumentException("fromNode not found in this graph.");
            Node to = Find(toNode) ?? throw new ArgumentException("toNode not found in this graph.");

            NodePort? outPort = from.Ports.FirstOrDefault(p => p.Name == fromPort && p.Direction == PortDirection.Output);
            NodePort? inPort = to.Ports.FirstOrDefault(p => p.Name == toPort && p.Direction == PortDirection.Input);

            if (outPort == null)
                throw new ArgumentException($"'{fromPort}' is not an output port on {from.GetType().Name}.");
            if (inPort == null)
                throw new ArgumentException($"'{toPort}' is not an input port on {to.GetType().Name}.");
            if (outPort.Type != inPort.Type)
                throw new InvalidOperationException(
                    $"Port type mismatch: {outPort.Type} output cannot feed a {inPort.Type} input.");

            if (_connections.Any(c => c.ToNodeId == toNode && c.ToPort == toPort))
                throw new InvalidOperationException(
                    $"'{toPort}' on this node already has an incoming connection; disconnect it first.");

            if (CanReach(toNode, fromNode))
                throw new InvalidOperationException("This connection would create a cycle.");

            var connection = new Connection(fromNode, fromPort, toNode, toPort);
            Transaction.Apply(() => _connections.Add(connection), () => _connections.Remove(connection), "connect");
            return connection;
        }

        /// <summary>Removes a connection; one that isn't in the graph is ignored.</summary>
        /// <param name="connection">The connection to remove.</param>
        public void Disconnect(Connection connection)
        {
            int index = _connections.IndexOf(connection);
            if (index < 0) return;

            Transaction.Apply(
                () => _connections.Remove(connection),
                () => _connections.Insert(System.Math.Min(index, _connections.Count), connection),
                "disconnect");
        }

        //whether `from` reaches `to` by following connections forward
        private bool CanReach(Guid from, Guid to)
        {
            var visited = new HashSet<Guid>();
            var queue = new Queue<Guid>();
            queue.Enqueue(from);

            while (queue.Count > 0)
            {
                Guid current = queue.Dequeue();
                if (current == to) return true;
                if (!visited.Add(current)) continue;

                foreach (Connection c in _connections.Where(c => c.FromNodeId == current))
                    queue.Enqueue(c.ToNodeId);
            }

            return false;
        }
    }
}
