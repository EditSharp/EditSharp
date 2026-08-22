using System;
using System.Collections.Generic;
using System.Linq;
 
namespace EditSharp.Components.Nodes
{
    public enum PortType { Image, Mask, Audio, Value }
    public enum PortDirection { Input, Output }
 
    /// <summary>A fixed port declared by a concrete Node type.</summary>
    public sealed class NodePort
    {
        public string Name { get; }
        public PortType Type { get; }
        public PortDirection Direction { get; }
 
        //true for a port that's allowed to sit unconnected at render time
        //(e.g. a filter node's "Mask" input, or a node's optional Value
        //modulation input) — see Graph.Connect and the bypass/inert-
        //input rules in the schema doc
        public bool Optional { get; }
 
        public NodePort(string name, PortType type, PortDirection direction, bool optional = false)
        {
            Name = name;
            Type = type;
            Direction = direction;
            Optional = optional;
        }
    }
 
    public sealed class Connection
    {
        public Guid FromNodeId { get; }
        public string FromPort { get; }
        public Guid ToNodeId { get; }
        public string ToPort { get; }
 
        internal Connection(Guid fromNodeId, string fromPort, Guid toNodeId, string toPort)
        {
            FromNodeId = fromNodeId;
            FromPort = fromPort;
            ToNodeId = toNodeId;
            ToPort = toPort;
        }
    }
 
    /// <summary>Which domain a graph's ports may use — see Graph.AddNode.</summary>
    public enum NodeDomain { Image, Audio }
 
    /// <summary>
    /// A directed graph of nodes — see the schema doc's "Effects:
    /// Node-Based Compositing Graph" section for the full design. The same
    /// primitives here power both the Image/Mask domain (VideoClip) and the
    /// Audio domain (AudioClip); NodeDomain is what keeps the two from
    /// ever being wired together, EXCEPT for domain-universal nodes (see
    /// InferDomain below), which is exactly how the "base node type for
    /// math functions that are universally applicable" the user asked for
    /// is implemented: ValueConstantNode/MathNode (see
    /// EditSharp.Components.Nodes.Math) declare only PortType.Value ports,
    /// which InferDomain treats as belonging to NEITHER domain specifically,
    /// and therefore addable to either.
    ///
    /// FUNDAMENTAL REWRITE ("clips are graphs"): there is no longer a
    /// second fixed anchor alongside OutputNode. The old ImageSourceNode/
    /// AudioSourceNode were each a single, mandatory, non-removable "the
    /// clip's content enters here" anchor — every clip's graph had EXACTLY
    /// one. That model assumed a clip was "media with a graph of effects
    /// bolted on". The corrected model is that a clip IS its graph, and a
    /// graph can have as many InputNodes as an author wants (a minimum of
    /// one for the graph to produce anything, enforced the same way it
    /// always effectively was — OutputNode must have SOME path reaching it
    /// at render time). See InputNode's own remarks.
    ///
    /// Encapsulation principle applies: Nodes/Connections are read-only
    /// externally, all mutation goes through AddNode/RemoveNode/Connect/
    /// Disconnect — which is what makes "no dangling references, no cycles,
    /// no type mismatches" an actual invariant rather than a convention a
    /// caller could violate by touching a public list directly.
    /// </summary>
    public sealed class Graph
    {
        public NodeDomain Domain { get; }
 
        private readonly List<Node> _nodes = [];
        private readonly List<Connection> _connections = [];
 
        public IReadOnlyList<Node> Nodes => _nodes;
        public IReadOnlyList<Connection> Connections => _connections;
 
        //the ONLY fixed anchor left — mandatory, not removable. Every INPUT
        //is now an ordinary node (see InputNode) instead of a second fixed
        //anchor the way it used to be.
        public OutputNode OutputNode { get; }
 
        /// <summary>
        /// Convenience access to every InputNode currently in this graph,
        /// without the caller having to filter Nodes by hand — see
        /// InputNode's own remarks.
        /// </summary>
        public IReadOnlyList<InputNode> InputNodes => _nodes.OfType<InputNode>().ToList();
 
        private Graph(NodeDomain domain, OutputNode outputNode)
        {
            Domain = domain;
            OutputNode = outputNode;
            _nodes.Add(outputNode);
        }
 
        /// <summary>
        /// A "normal/default" video clip: a single InputNode you supply
        /// (a MediaSourceNode wrapping a Source, in the common case, but
        /// any InputNode works — see VideoClip's own static factories),
        /// wired through the two nodes every visual clip gets by default:
        /// TintNode (what replaced the old flat Modulate/tint-and-opacity
        /// property) and TransformNode (what replaced the old flat
        /// ClipTransform property — its data now lives ON the node itself,
        /// not on the owning clip). Both are ordinary, removable,
        /// reorderable nodes beyond this default, exactly like GainNode
        /// always has been on the audio side.
        /// </summary>
        public static Graph CreateVideoGraph(InputNode input)
        {
            var graph = new Graph(NodeDomain.Image, new ImageOutputNode());
 
            graph.AddNode(input);
            var tint = (Effects.TintNode)graph.AddNode(new Effects.TintNode());
            var transform = (Effects.TransformNode)graph.AddNode(new Effects.TransformNode());
 
            graph.Connect(input.Id, "Image", tint.Id, "Image");
            graph.Connect(tint.Id, "Image", transform.Id, "Image");
            graph.Connect(transform.Id, "Image", graph.OutputNode.Id, "Image");
 
            return graph;
        }
 
        /// <summary>
        /// A "normal/default" audio clip: a single InputNode you supply
        /// (a MediaAudioSourceNode wrapping a Source, in the common case —
        /// see AudioClip's own static factories), wired through GainNode —
        /// what replaced the old flat AudioClip.Volume field, unchanged
        /// from before this rewrite.
        /// </summary>
        public static Graph CreateAudioGraph(InputNode input)
        {
            var graph = new Graph(NodeDomain.Audio, new AudioOutputNode());
 
            graph.AddNode(input);
            var gain = (Effects.GainNode)graph.AddNode(new Effects.GainNode());
 
            graph.Connect(input.Id, "Audio", gain.Id, "Audio");
            graph.Connect(gain.Id, "Audio", graph.OutputNode.Id, "Audio");
 
            return graph;
        }
 
        /// <summary>
        /// An empty graph with no InputNode at all yet — for building a
        /// fully custom multi-input graph from scratch (see
        /// VideoClip.CreateCustom/AudioClip.CreateCustom). Not renderable
        /// until at least one node reaches OutputNode — same as any other
        /// disconnected-Output state, not a special case.
        /// </summary>
        public static Graph CreateEmptyVideoGraph() => new(NodeDomain.Image, new ImageOutputNode());
        public static Graph CreateEmptyAudioGraph() => new(NodeDomain.Audio, new AudioOutputNode());
 
        /// <summary>Deep copy — a fresh graph with fresh node Ids, connections remapped to match.</summary>
        public Graph Duplicate()
        {
            var map = new Dictionary<Guid, Node>();
 
            Node NewOf(Node original)
            {
                Node copy = original.Duplicate();
                map[original.Id] = copy;
                return copy;
            }
 
            var newOutput = (OutputNode)NewOf(OutputNode);
            var graph = new Graph(Domain, newOutput);
 
            foreach (Node node in _nodes)
            {
                if (ReferenceEquals(node, OutputNode)) continue;
 
                Node copy = NewOf(node);
                graph._nodes.Add(copy);
            }
 
            foreach (Connection c in _connections)
            {
                graph._connections.Add(new Connection(
                    map[c.FromNodeId].Id, c.FromPort, map[c.ToNodeId].Id, c.ToPort));
            }
 
            return graph;
        }
 
        private Node? Find(Guid id) => _nodes.FirstOrDefault(n => n.Id == id);
 
        /// <summary>
        /// A node's domain is inferred from its own ports: any Audio port
        /// makes it Audio-only, any Image/Mask port makes it Image-only (a
        /// node can't declare both — it would belong to neither graph
        /// cleanly), and a node with ONLY Value ports (or no ports at all)
        /// is domain-UNIVERSAL, returned as null here — addable to either
        /// graph type. This null case is exactly how ValueConstantNode/
        /// MathNode work identically inside an Image-domain clip's graph
        /// and an Audio-domain clip's graph.
        /// </summary>
        private static NodeDomain? InferDomain(Node node)
        {
            bool hasAudio = node.Ports.Any(p => p.Type == PortType.Audio);
            bool hasImage = node.Ports.Any(p => p.Type is PortType.Image or PortType.Mask);
 
            if (hasAudio && hasImage)
                throw new InvalidOperationException(
                    $"{node.GetType().Name} declares both Audio and Image/Mask ports — a node must " +
                    "belong to exactly one signal domain (or be domain-universal, via Value-only ports).");
 
            if (hasAudio) return NodeDomain.Audio;
            if (hasImage) return NodeDomain.Image;
            return null; //universal
        }
 
        /// <summary>Rejects a node whose port domain doesn't match this graph's own (universal nodes always pass).</summary>
        public Node AddNode(Node node)
        {
            NodeDomain? nodeDomain = InferDomain(node);
 
            if (nodeDomain != null && nodeDomain != Domain)
                throw new InvalidOperationException(
                    $"Cannot add a {nodeDomain} node to a {Domain} Graph.");
 
            _nodes.Add(node);
            return node;
        }
 
        /// <summary>
        /// Rejects OutputNode — the one fixed anchor; everything else
        /// (including every InputNode, TintNode/TransformNode/GainNode,
        /// which are defaults, not anchors) is fully removable. Also
        /// removes every Connection that referenced this node, both
        /// incoming and outgoing, so the graph never carries a Connection
        /// pointing at a Guid that no longer resolves to anything in
        /// Nodes.
        /// </summary>
        public void RemoveNode(Node node)
        {
            if (ReferenceEquals(node, OutputNode))
                throw new InvalidOperationException("OutputNode cannot be removed from a Graph.");
 
            _nodes.Remove(node);
            _connections.RemoveAll(c => c.FromNodeId == node.Id || c.ToNodeId == node.Id);
        }
 
        /// <summary>
        /// Validates port types match, no cycle results, and the target
        /// input port doesn't already have an incoming connection (every
        /// input port — OutputNode included — accepts at most one; an
        /// interactive graph editor disconnects before reconnecting rather
        /// than this silently accumulating extra edges into one input).
        /// Zero connections into a mandatory input is a valid, if
        /// incomplete, intermediate state — see the schema doc's own
        /// remarks on why this isn't rejected at edit time.
        /// </summary>
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
                    $"'{toPort}' on this node already has an incoming connection — disconnect it first.");
 
            if (CanReach(toNode, fromNode))
                throw new InvalidOperationException("This connection would create a cycle.");
 
            var connection = new Connection(fromNode, fromPort, toNode, toPort);
            _connections.Add(connection);
            return connection;
        }
 
        public void Disconnect(Connection connection) => _connections.Remove(connection);
 
        /// <summary>True if `from` can reach `to` by following existing Connections forward.</summary>
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
 