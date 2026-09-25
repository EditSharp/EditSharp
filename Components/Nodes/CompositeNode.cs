using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Editing;
using EditSharp.History;

namespace EditSharp.Components.Nodes
{
    /// <summary>An inner node's input port, shown on the composite under `Name`.</summary>
    public sealed class ExposedPort
    {
        public Guid Node { get; }
        public string Port { get; }
        public string Name { get; }

        internal ExposedPort(Guid node, string port, string name)
        {
            Node = node;
            Port = port;
            Name = name;
        }
    }

    /// <summary>An inner node's editable property, shown on the composite under `Name`.</summary>
    public sealed class ExposedProperty
    {
        public Guid Node { get; }
        public string Property { get; }
        public string Name { get; }

        internal ExposedProperty(Guid node, string property, string name)
        {
            Node = node;
            Property = property;
            Name = name;
        }
    }

    /// <summary>
    /// A graph folded into one node — what a user gets by assembling a
    /// graph and saving it as a node of their own. It has the same domain
    /// as the graph inside it, one output (whatever feeds the inner
    /// graph's OutputNode), and as inputs exactly the inner input ports
    /// it chose to expose. Its editable properties are the inner ones it
    /// chose to expose, each under its own name (see IInspectable).
    ///
    /// Nothing evaluates a composite as such: Graph.Flattened replaces
    /// every composite with the nodes inside it before an evaluator sees
    /// the graph, so the effect and audio pipelines never know it was
    /// there. The inner nodes are the same objects, so their content,
    /// keyframes and in-points are found and shifted like any other's
    /// (see Graph.AllNodes).
    ///
    /// Not yet persisted — that arrives with the project file format.
    /// </summary>
    public sealed class CompositeNode : Node, IInspectable
    {
        string _name;
        [Editable("Name", Order = -90)]
        public string Name { get => _name; set => Transaction.Set(this, ref _name, value, static (o, v) => o._name = v); }

        public Graph Inner { get; }

        private readonly List<ExposedPort> _inputs = [];
        private readonly List<ExposedProperty> _properties = [];

        public IReadOnlyList<ExposedPort> Inputs => _inputs;
        public IReadOnlyList<ExposedProperty> ExposedProperties => _properties;

        public CompositeNode(Graph inner, string name = "Custom node")
        {
            Inner = inner ?? throw new ArgumentNullException(nameof(inner));
            Inner.Composite = this;
            _name = name;
        }

        /// <summary>The composite's single output: the inner graph's own output, in the graph's domain.</summary>
        public NodePort Output => Inner.Domain == NodeDomain.Image
            ? new NodePort("Image", PortType.Image, PortDirection.Output)
            : new NodePort("Audio", PortType.Audio, PortDirection.Output);

        public override IReadOnlyList<NodePort> Ports
        {
            get
            {
                List<NodePort> ports = [];

                foreach (ExposedPort exposed in _inputs)
                {
                    NodePort? inner = InnerPort(exposed);
                    if (inner is not null) ports.Add(new NodePort(exposed.Name, inner.Type, PortDirection.Input, inner.Optional));
                }

                ports.Add(Output);
                return ports;
            }
        }

        internal Node? InnerNode(Guid id) => Inner.Nodes.FirstOrDefault(n => n.Id == id);

        internal NodePort? InnerPort(ExposedPort exposed)
            => InnerNode(exposed.Node)?.Ports.FirstOrDefault(p => p.Name == exposed.Port && p.Direction == PortDirection.Input);

        // ---------------------------------------------------------------
        // Exposing
        // ---------------------------------------------------------------

        /// <summary>Shows an inner node's unconnected input port as one of this node's own. Recorded.</summary>
        public ExposedPort ExposeInput(Node node, string port, string? name = null)
        {
            RequireInner(node);

            NodePort inner = node.Ports.FirstOrDefault(p => p.Name == port && p.Direction == PortDirection.Input)
                ?? throw new ArgumentException($"'{port}' is not an input port on {node.GetType().Name}.", nameof(port));

            if (Inner.Connections.Any(c => c.ToNodeId == node.Id && c.ToPort == port))
                throw new InvalidOperationException($"'{port}' is already fed from inside the composite.");

            name ??= inner.Name;

            if (_inputs.Any(i => i.Name == name))
                throw new InvalidOperationException($"This composite already has an input called '{name}'.");

            ExposedPort exposed = new(node.Id, port, name);
            Transaction.Apply(() => _inputs.Add(exposed), () => _inputs.Remove(exposed), "expose input");

            return exposed;
        }

        public void HideInput(ExposedPort exposed)
        {
            int index = _inputs.IndexOf(exposed);
            if (index < 0) return;

            Transaction.Apply(
                () => _inputs.Remove(exposed),
                () => _inputs.Insert(System.Math.Min(index, _inputs.Count), exposed),
                "hide input");
        }

        /// <summary>Shows an inner node's editable property as one of this node's own. Recorded.</summary>
        public ExposedProperty ExposeProperty(Node node, string property, string? name = null)
        {
            RequireInner(node);

            PropertyDescriptor descriptor = Inspect.Find(node, property)
                ?? throw new ArgumentException($"'{property}' is not an editable property on {node.GetType().Name}.", nameof(property));

            name ??= descriptor.DisplayName;

            if (_properties.Any(p => p.Name == name))
                throw new InvalidOperationException($"This composite already exposes a property called '{name}'.");

            ExposedProperty exposed = new(node.Id, property, name);
            Transaction.Apply(() => _properties.Add(exposed), () => _properties.Remove(exposed), "expose property");

            return exposed;
        }

        public void HideProperty(ExposedProperty exposed)
        {
            int index = _properties.IndexOf(exposed);
            if (index < 0) return;

            Transaction.Apply(
                () => _properties.Remove(exposed),
                () => _properties.Insert(System.Math.Min(index, _properties.Count), exposed),
                "hide property");
        }

        private void RequireInner(Node node)
        {
            if (!Inner.Nodes.Contains(node))
                throw new ArgumentException("That node is not inside this composite.", nameof(node));
        }

        // ---------------------------------------------------------------
        // IInspectable — this node's own properties, then the exposed ones,
        // each redirected to the inner node that really holds it
        // ---------------------------------------------------------------

        public IReadOnlyList<PropertyDescriptor> Properties
        {
            get
            {
                List<PropertyDescriptor> list = [.. Inspect.Of(typeof(CompositeNode))];

                foreach (ExposedProperty exposed in _properties)
                {
                    Node? node = InnerNode(exposed.Node);
                    if (node is null) continue;

                    PropertyDescriptor? descriptor = Inspect.Find(node, exposed.Property);
                    if (descriptor is null) continue;

                    list.Add(descriptor.Through(_ => node, exposed.Name));
                }

                return list;
            }
        }

        public override IEnumerable<IAnimatable> Animatables => Inner.Animatables;

        public override Node Duplicate() => Transaction.Suppressed(() =>
        {
            Graph inner = Inner.Duplicate(out Dictionary<Guid, Guid> ids);
            CompositeNode copy = new(inner, Name) { Enabled = Enabled };

            foreach (ExposedPort p in _inputs)
                if (ids.TryGetValue(p.Node, out Guid id)) copy._inputs.Add(new ExposedPort(id, p.Port, p.Name));

            foreach (ExposedProperty p in _properties)
                if (ids.TryGetValue(p.Node, out Guid id)) copy._properties.Add(new ExposedProperty(id, p.Property, p.Name));

            return (Node)copy;
        });
    }
}
