using System;
using System.Collections.Generic;
using System.Linq;
 
namespace EditSharp.Components.Effects
{
    public enum PortType { Image, Mask, Audio, Value }
    public enum PortDirection { Input, Output }
 
    /// <summary>A fixed port declared by a concrete EffectNode type.</summary>
    public sealed class NodePort
    {
        public string Name { get; }
        public PortType Type { get; }
        public PortDirection Direction { get; }
 
        //true for a port that's allowed to sit unconnected at render time
        //(e.g. a filter node's "Mask" input, or a node's optional Value
        //modulation input) — see EffectGraph.Connect and the bypass/inert-
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
 
    public abstract class EffectNode
    {
        public Guid Id { get; } = Guid.NewGuid();
 
        //fixed set, declared by the concrete node type
        public abstract IReadOnlyList<NodePort> Ports { get; }
 
        //bypass — see EffectGraph's class remarks
        public bool Enabled { get; set; } = true;
 
        /// <summary>
        /// Deep copy with a FRESH Id — a duplicated clip's graph must not
        /// share node identity with the original, or a Connection recorded
        /// against one graph could be mistaken for referencing a node in
        /// the other.
        /// </summary>
        public abstract EffectNode Duplicate();
    }
 
    /// <summary>
    /// FUNDAMENTAL REWRITE: a node that ORIGINATES a clip's content, rather
    /// than receiving it from upstream — see the schema doc's "clips are
    /// graphs" model. This replaces the old fixed, mandatory
    /// ImageSourceNode/AudioSourceNode anchors entirely. An InputNode is
    /// just an ordinary node: fully addable, removable, and rewireable like
    /// any other. A graph needs at least one InputNode actually reaching
    /// Output to render anything, but that's not tracked as a separate
    /// structural invariant here — it falls out naturally from the exact
    /// same "Output has no incoming connection" check every evaluator
    /// already performs (see EffectGraphEvaluatorSk/AudioEffectGraphEvaluator),
    /// the same way it always has for a disconnected Output. This is also
    /// what makes a MULTI-input graph (two media sources merged via
    /// MergeNode/AudioMixNode, say) nothing special: it's just a graph
    /// with more than one InputNode, no different in kind from one with a
    /// single InputNode.
    ///
    /// Concrete video-domain InputNodes: MediaSourceNode, TextInputNode,
    /// ColorGeneratorInputNode, NoiseInputNode, TimelineVideoInputNode (see
    /// VideoEffectNodes.cs). Concrete audio-domain InputNodes:
    /// MediaAudioSourceNode, ToneGeneratorInputNode, TimelineAudioInputNode
    /// (see AudioEffectNodes.cs). These are what replaced the old
    /// VideoClip.Source-is-the-whole-clip / TextClip / GeneratorClip /
    /// NoiseClip / TimelineVideoClip / TimelineAudioClip Clip subtypes —
    /// the ONLY remaining concrete Clip subtypes are VideoClip and
    /// AudioClip, and what used to distinguish those Clip subtypes from
    /// each other is now just which InputNode(s) happen to be wired into
    /// an otherwise perfectly ordinary EffectGraph.
    /// </summary>
    public abstract class InputNode : EffectNode { }
 
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
 
    /// <summary>Which domain a graph's ports may use — see EffectGraph.AddNode.</summary>
    public enum EffectDomain { Image, Audio }
 
    /// <summary>
    /// A directed graph of effect nodes — see the schema doc's "Effects:
    /// Node-Based Compositing Graph" section for the full design. The same
    /// primitives here power both the Image/Mask domain (VideoClip) and the
    /// Audio domain (AudioClip); EffectDomain is what keeps the two from
    /// ever being wired together, EXCEPT for domain-universal nodes (see
    /// InferDomain below), which is exactly how the "base node type for
    /// math functions that are universally applicable" the user asked for
    /// is implemented: ValueConstantNode/MathNode (see ValueNodes.cs)
    /// declare only PortType.Value ports, which InferDomain treats as
    /// belonging to NEITHER domain specifically, and therefore addable to
    /// either.
    ///
    /// FUNDAMENTAL REWRITE ("clips are graphs"): there is no longer a
    /// second fixed anchor alongside Output. The old ImageSourceNode/
    /// AudioSourceNode were each a single, mandatory, non-removable "the
    /// clip's content enters here" anchor — every clip's graph had EXACTLY
    /// one. That model assumed a clip was "media with a graph of effects
    /// bolted on". The corrected model is that a clip IS its graph, and a
    /// graph can have as many InputNodes as an author wants (a minimum of
    /// one for the graph to produce anything, enforced the same way it
    /// always effectively was — Output must have SOME path reaching it at
    /// render time). See InputNode's own remarks.
    ///
    /// Encapsulation principle applies: Nodes/Connections are read-only
    /// externally, all mutation goes through AddNode/RemoveNode/Connect/
    /// Disconnect — which is what makes "no dangling references, no cycles,
    /// no type mismatches" an actual invariant rather than a convention a
    /// caller could violate by touching a public list directly.
    /// </summary>
    public sealed class EffectGraph
    {
        public EffectDomain Domain { get; }
 
        private readonly List<EffectNode> _nodes = [];
        private readonly List<Connection> _connections = [];
 
        public IReadOnlyList<EffectNode> Nodes => _nodes;
        public IReadOnlyList<Connection> Connections => _connections;
 
        //the ONLY fixed anchor left — mandatory, not removable. Every INPUT
        //is now an ordinary node (see InputNode) instead of a second fixed
        //anchor the way it used to be.
        public EffectNode Output { get; }
 
        private EffectGraph(EffectDomain domain, EffectNode output)
        {
            Domain = domain;
            Output = output;
            _nodes.Add(output);
        }
 
        /// <summary>
        /// A "normal/default" video clip: a single InputNode you supply
        /// (a MediaSourceNode wrapping a Source, in the common case, but
        /// any InputNode works — see VideoClip's own static factories),
        /// wired through the two nodes every visual clip gets by default:
        /// TintNode (what replaced the old flat Modulate/tint-and-opacity
        /// property — see VideoEffectNodes.cs) and TransformNode (what
        /// replaced the old flat ClipTransform property — its data now
        /// lives ON the node itself, not on the owning clip). Both are
        /// ordinary, removable, reorderable nodes beyond this default,
        /// exactly like GainNode always has been on the audio side.
        /// </summary>
        public static EffectGraph CreateVideoGraph(InputNode input)
        {
            var graph = new EffectGraph(EffectDomain.Image, new ImageOutputNode());
 
            graph.AddNode(input);
            var tint = (TintNode)graph.AddNode(new TintNode());
            var transform = (TransformNode)graph.AddNode(new TransformNode());
 
            graph.Connect(input.Id, "Image", tint.Id, "Image");
            graph.Connect(tint.Id, "Image", transform.Id, "Image");
            graph.Connect(transform.Id, "Image", graph.Output.Id, "Image");
 
            return graph;
        }
 
        /// <summary>
        /// A "normal/default" audio clip: a single InputNode you supply
        /// (a MediaAudioSourceNode wrapping a Source, in the common case —
        /// see AudioClip's own static factories), wired through GainNode —
        /// what replaced the old flat AudioClip.Volume field, unchanged
        /// from before this rewrite.
        /// </summary>
        public static EffectGraph CreateAudioGraph(InputNode input)
        {
            var graph = new EffectGraph(EffectDomain.Audio, new AudioOutputNode());
 
            graph.AddNode(input);
            var gain = (GainNode)graph.AddNode(new GainNode());
 
            graph.Connect(input.Id, "Audio", gain.Id, "Audio");
            graph.Connect(gain.Id, "Audio", graph.Output.Id, "Audio");
 
            return graph;
        }
 
        /// <summary>
        /// An empty graph with no InputNode at all yet — for building a
        /// fully custom multi-input graph from scratch (see
        /// VideoClip.CreateCustom/AudioClip.CreateCustom). Not renderable
        /// until at least one node reaches Output — same as any other
        /// disconnected-Output state, not a special case.
        /// </summary>
        public static EffectGraph CreateEmptyVideoGraph() => new(EffectDomain.Image, new ImageOutputNode());
        public static EffectGraph CreateEmptyAudioGraph() => new(EffectDomain.Audio, new AudioOutputNode());
 
        /// <summary>Deep copy — a fresh graph with fresh node Ids, connections remapped to match.</summary>
        public EffectGraph Duplicate()
        {
            var map = new Dictionary<Guid, EffectNode>();
 
            EffectNode NewOf(EffectNode original)
            {
                EffectNode copy = original.Duplicate();
                map[original.Id] = copy;
                return copy;
            }
 
            EffectNode newOutput = NewOf(Output);
            var graph = new EffectGraph(Domain, newOutput);
 
            foreach (EffectNode node in _nodes)
            {
                if (ReferenceEquals(node, Output)) continue;
 
                EffectNode copy = NewOf(node);
                graph._nodes.Add(copy);
            }
 
            foreach (Connection c in _connections)
            {
                graph._connections.Add(new Connection(
                    map[c.FromNodeId].Id, c.FromPort, map[c.ToNodeId].Id, c.ToPort));
            }
 
            return graph;
        }
 
        private EffectNode? Find(Guid id) => _nodes.FirstOrDefault(n => n.Id == id);
 
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
        private static EffectDomain? InferDomain(EffectNode node)
        {
            bool hasAudio = node.Ports.Any(p => p.Type == PortType.Audio);
            bool hasImage = node.Ports.Any(p => p.Type is PortType.Image or PortType.Mask);
 
            if (hasAudio && hasImage)
                throw new InvalidOperationException(
                    $"{node.GetType().Name} declares both Audio and Image/Mask ports — a node must " +
                    "belong to exactly one signal domain (or be domain-universal, via Value-only ports).");
 
            if (hasAudio) return EffectDomain.Audio;
            if (hasImage) return EffectDomain.Image;
            return null; //universal
        }
 
        /// <summary>Rejects a node whose port domain doesn't match this graph's own (universal nodes always pass).</summary>
        public EffectNode AddNode(EffectNode node)
        {
            EffectDomain? nodeDomain = InferDomain(node);
 
            if (nodeDomain != null && nodeDomain != Domain)
                throw new InvalidOperationException(
                    $"Cannot add a {nodeDomain} node to a {Domain} EffectGraph.");
 
            _nodes.Add(node);
            return node;
        }
 
        /// <summary>
        /// Rejects Output — the one fixed anchor; everything else
        /// (including every InputNode, TintNode/TransformNode/GainNode,
        /// which are defaults, not anchors) is fully removable. Also
        /// removes every Connection that referenced this node, both
        /// incoming and outgoing, so the graph never carries a Connection
        /// pointing at a Guid that no longer resolves to anything in
        /// Nodes.
        /// </summary>
        public void RemoveNode(EffectNode node)
        {
            if (ReferenceEquals(node, Output))
                throw new InvalidOperationException("Output cannot be removed from an EffectGraph.");
 
            _nodes.Remove(node);
            _connections.RemoveAll(c => c.FromNodeId == node.Id || c.ToNodeId == node.Id);
        }
 
        /// <summary>
        /// Validates port types match, no cycle results, and the target
        /// input port doesn't already have an incoming connection (every
        /// input port — Output included — accepts at most one; an
        /// interactive graph editor disconnects before reconnecting rather
        /// than this silently accumulating extra edges into one input).
        /// Zero connections into a mandatory input is a valid, if
        /// incomplete, intermediate state — see the schema doc's own
        /// remarks on why this isn't rejected at edit time.
        /// </summary>
        public Connection Connect(Guid fromNode, string fromPort, Guid toNode, string toPort)
        {
            EffectNode from = Find(fromNode) ?? throw new ArgumentException("fromNode not found in this graph.");
            EffectNode to = Find(toNode) ?? throw new ArgumentException("toNode not found in this graph.");
 
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
 