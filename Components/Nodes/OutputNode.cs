namespace EditSharp.Components.Nodes
{
    /// <summary>
    /// Common base for the one fixed, mandatory, non-removable anchor every
    /// graph has — ImageOutputNode for an Image-domain graph, AudioOutputNode
    /// for an Audio-domain graph (see Graph.OutputNode). Carries no members
    /// of its own beyond what Node already provides; it exists purely so
    /// Graph.OutputNode can be typed as "the output anchor" rather than the
    /// generic base Node type.
    /// </summary>
    public abstract class OutputNode : Node { }
}
