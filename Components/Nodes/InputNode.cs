namespace EditSharp.Components.Nodes
{
    /// <summary>
    /// FUNDAMENTAL REWRITE: a node that ORIGINATES a clip's content, rather
    /// than receiving it from upstream — see the schema doc's "clips are
    /// graphs" model. This replaces the old fixed, mandatory
    /// ImageSourceNode/AudioSourceNode anchors entirely. An InputNode is
    /// just an ordinary node: fully addable, removable, and rewireable like
    /// any other. A graph needs at least one InputNode actually reaching
    /// OutputNode to render anything, but that's not tracked as a separate
    /// structural invariant here — it falls out naturally from the exact
    /// same "Output has no incoming connection" check every evaluator
    /// already performs (see EffectGraphEvaluatorSk/AudioEffectGraphEvaluator),
    /// the same way it always has for a disconnected Output. This is also
    /// what makes a MULTI-input graph (two media sources merged via
    /// MergeNode/AudioMixNode, say) nothing special: it's just a graph
    /// with more than one InputNode, no different in kind from one with a
    /// single InputNode.
    ///
    /// Concrete video-domain InputNodes: VideoSourceNode, TextInputNode,
    /// ColorGeneratorInputNode, NoiseInputNode, TimelineVideoInputNode (see
    /// EditSharp.Components.Nodes.Sources.Video). Concrete audio-domain
    /// InputNodes: AudioSourceNode, ToneGeneratorInputNode,
    /// TimelineAudioInputNode (see EditSharp.Components.Nodes.Sources.Audio).
    /// These are what replaced the old VideoClip.Source-is-the-whole-clip /
    /// TextClip / GeneratorClip / NoiseClip / TimelineVideoClip /
    /// TimelineAudioClip Clip subtypes — the ONLY remaining concrete Clip
    /// subtypes are VideoClip and AudioClip, and what used to distinguish
    /// those Clip subtypes from each other is now just which InputNode(s)
    /// happen to be wired into an otherwise perfectly ordinary Graph.
    ///
    /// Also see Graph's own convenience InputNodes property, for reading
    /// every InputNode currently in a graph without having to filter
    /// Nodes by hand.
    /// </summary>
    public abstract class InputNode : Node { }
}
 