namespace EditSharp.Components.Sources
{
    /// <summary>Whether sources that have proxies read the proxy or the original.</summary>
    /// <remarks>Kinds with no proxy (a still image, a generator) ignore this. Random access (scrubbing, reverse) always reads the proxy whatever the mode, since decoding an original at an arbitrary position every frame is too slow.</remarks>
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
