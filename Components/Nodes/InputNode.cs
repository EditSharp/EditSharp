namespace EditSharp.Components.Nodes
{
    /// <summary>A node that brings content into a graph, rather than taking it from upstream.</summary>
    /// <remarks>A graph can have any number of inputs, or none; one that reaches the output node is what makes a clip show or play anything. <see cref="Sources.VideoSourceNode"/> and <see cref="Sources.AudioSourceNode"/> are the inputs; the kind of content comes from their Source.</remarks>
    public abstract class InputNode : Node { }
}
