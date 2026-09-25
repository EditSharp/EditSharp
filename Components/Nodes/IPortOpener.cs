namespace EditSharp.Components.Nodes
{
    /// <summary>A node that can add ports on request, so <see cref="Graph.ReplaceNode"/> can wire it in place of a node with more ports than it declares.</summary>
    /// <remarks>Nothing implements it yet; a node with a variable number of inputs, such as a merge of any number of images, would.</remarks>
    public interface IPortOpener
    {
        /// <summary>Opens a port for a wire that has nowhere else to go.</summary>
        /// <param name="name">The port's name on the node being replaced; a hint, since the opened port may be called something else.</param>
        /// <param name="type">What the port must carry.</param>
        /// <param name="direction">Whether it must take a wire in or send one out.</param>
        /// <returns>The opened port, now in <see cref="Node.Ports"/>; null when the node can't open one of that type and direction.</returns>
        NodePort? TryOpenPort(string name, PortType type, PortDirection direction);
    }
}
