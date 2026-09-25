using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Editing;
using EditSharp.History;

namespace EditSharp.Components.Nodes
{
    /// <summary>An inner node's input port, shown as one of a <see cref="CompositeNode"/>'s own.</summary>
    public sealed class ExposedPort
    {
        /// <summary>The <see cref="Nodes.Node.Id"/> of the inner node.</summary>
        public Guid Node { get; }

        /// <summary>The inner node's input port.</summary>
        public string Port { get; }

        /// <summary>The port's name on the composite.</summary>
        public string Name { get; }

        internal ExposedPort(Guid node, string port, string name)
        {
            Node = node;
            Port = port;
            Name = name;
        }
    }

    /// <summary>An inner node's editable property, shown as one of a <see cref="CompositeNode"/>'s own.</summary>
    public sealed class ExposedProperty
    {
        /// <summary>The <see cref="Nodes.Node.Id"/> of the inner node.</summary>
        public Guid Node { get; }

        /// <summary>The name of the inner node's property.</summary>
        public string Property { get; }

        /// <summary>The property's name on the composite.</summary>
        public string Name { get; }

        internal ExposedProperty(Guid node, string property, string name)
        {
            Node = node;
            Property = property;
            Name = name;
        }
    }

    /// <summary>A graph folded into one node, such as a custom effect built from other nodes.</summary>
    /// <remarks>
    /// Its output is whatever feeds the inner graph's output node, in the inner
    /// graph's domain. Its inputs are the inner input ports it exposes, and its
    /// editable properties are its <see cref="Name"/> plus the inner properties it
    /// exposes, each under its own name. Evaluation never sees a composite:
    /// <see cref="Graph.Flattened"/> replaces it with the nodes inside it first.
    /// </remarks>
    [NodeKind("composite", DisplayName = "Custom node", Listed = false)]
    public sealed class CompositeNode : Node, IInspectable
    {
        string _name;
        /// <summary>The node's name, as editors show it.</summary>
        [Editable("Name", Order = -90)]
        public string Name { get => _name; set => Transaction.Set(this, ref _name, value, static (o, v) => o._name = v); }

        /// <summary>The graph inside the node.</summary>
        public Graph Inner { get; }

        private readonly List<ExposedPort> _inputs = [];
        private readonly List<ExposedProperty> _properties = [];

        /// <summary>The inner input ports shown as this node's inputs, in order.</summary>
        public IReadOnlyList<ExposedPort> Inputs => _inputs;

        /// <summary>The inner properties shown as this node's own, in order.</summary>
        public IReadOnlyList<ExposedProperty> ExposedProperties => _properties;

        /// <summary>Folds a graph into a node.</summary>
        /// <param name="inner">The graph to fold; the node takes it over.</param>
        /// <param name="name">The node's name.</param>
        /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
        public CompositeNode(Graph inner, string name = "Custom node")
        {
            Inner = inner ?? throw new ArgumentNullException(nameof(inner));
            Inner.Composite = this;
            _name = name;
        }

        /// <summary>The node's one output: Image or Audio, matching the inner graph.</summary>
        public NodePort Output => Inner.Domain == NodeDomain.Image
            ? new NodePort("Image", PortType.Image, PortDirection.Output)
            : new NodePort("Audio", PortType.Audio, PortDirection.Output);

        /// <inheritdoc/>
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

        // ---- exposing ----

        /// <summary>Shows an inner node's unconnected input port as one of this node's inputs.</summary>
        /// <param name="node">The inner node.</param>
        /// <param name="port">The name of its input port.</param>
        /// <param name="name">The input's name on this node; null uses the port's name.</param>
        /// <returns>The exposed port.</returns>
        /// <exception cref="ArgumentException"><paramref name="node"/> isn't inside this composite, or has no such input port.</exception>
        /// <exception cref="InvalidOperationException">The port is already connected inside the composite, or this node already has an input with that name.</exception>
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

        /// <summary>Stops showing an exposed input; one that isn't exposed is ignored.</summary>
        /// <param name="exposed">The exposed input.</param>
        public void HideInput(ExposedPort exposed)
        {
            int index = _inputs.IndexOf(exposed);
            if (index < 0) return;

            Transaction.Apply(
                () => _inputs.Remove(exposed),
                () => _inputs.Insert(System.Math.Min(index, _inputs.Count), exposed),
                "hide input");
        }

        /// <summary>Shows an inner node's editable property as one of this node's own.</summary>
        /// <param name="node">The inner node.</param>
        /// <param name="property">The name of its property.</param>
        /// <param name="name">The property's name on this node; null uses its display name.</param>
        /// <returns>The exposed property.</returns>
        /// <exception cref="ArgumentException"><paramref name="node"/> isn't inside this composite, or has no such editable property.</exception>
        /// <exception cref="InvalidOperationException">This node already exposes a property with that name.</exception>
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

        /// <summary>Stops showing an exposed property; one that isn't exposed is ignored.</summary>
        /// <param name="exposed">The exposed property.</param>
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

        //an inner node was replaced (see Graph.ReplaceNode): its exposures follow the replacement where it has
        //the port or property, and are hidden where it doesn't, along with any outer wire into a hidden input
        internal void RemapExposures(Node old, Node replacement)
        {
            foreach (ExposedPort exposed in _inputs.Where(p => p.Node == old.Id).ToList())
            {
                bool has = replacement.Ports.Any(p => p.Name == exposed.Port && p.Direction == PortDirection.Input);

                if (!has)
                {
                    foreach (Connection wire in Graph?.Connections.Where(c => c.ToNodeId == Id && c.ToPort == exposed.Name).ToList() ?? [])
                        Graph!.Disconnect(wire);

                    HideInput(exposed);
                    continue;
                }

                int index = _inputs.IndexOf(exposed);
                ExposedPort moved = new(replacement.Id, exposed.Port, exposed.Name);
                Transaction.Apply(() => _inputs[index] = moved, () => _inputs[index] = exposed, "remap exposed input");
            }

            foreach (ExposedProperty exposed in _properties.Where(p => p.Node == old.Id).ToList())
            {
                if (Inspect.Find(replacement, exposed.Property) is null)
                {
                    HideProperty(exposed);
                    continue;
                }

                int index = _properties.IndexOf(exposed);
                ExposedProperty moved = new(replacement.Id, exposed.Property, exposed.Name);
                Transaction.Apply(() => _properties[index] = moved, () => _properties[index] = exposed, "remap exposed property");
            }
        }

        // ---- IInspectable ----

        /// <summary>This node's own properties, then the exposed ones, each editing the inner node that holds it.</summary>
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

        /// <inheritdoc/>
        public override IEnumerable<IAnimatable> Animatables => Inner.Animatables;

        /// <inheritdoc/>
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
