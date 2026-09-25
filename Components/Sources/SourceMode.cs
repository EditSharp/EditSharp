namespace EditSharp.Components.Sources
{
    /// <summary>
    /// Whether proxy-capable sources read their proxy or their original.
    /// Kinds with no notion of a proxy (a still image, a 3D scene) ignore
    /// this and always render from source. Random access (scrubbing, reverse)
    /// always needs a proxy regardless of mode; decoding an original at an
    /// arbitrary position per frame isn't viable.
    /// </summary>
    public enum SourceMode
    {
        /// <summary>Only proxies; a frame without one is ProxyPending while a build is under way, ProxyMissing otherwise.</summary>
        ProxiesOnly,

        /// <summary>The proxy wherever it covers the frame, the original everywhere else; decided frame by frame.</summary>
        ProxiesAndSource,

        /// <summary>Only originals for sequential reads (random access still needs a proxy).</summary>
        SourceOnly,
    }
}
