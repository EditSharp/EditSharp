using System;

namespace EditSharp.Caching.Proxy
{
    public enum ProxyFormat
    {
        /// <summary>.esrp, 7-bit indexed against one shared palette. Smallest; lowest colour fidelity; no subprocess to read.</summary>
        EsrpDelta7,

        /// <summary>.esrp, full RGBA8888. Large; full fidelity at proxy size; no subprocess to read.</summary>
        EsrpRgba,

        /// <summary>DNxHR in a MOV; all-intra, widely compatible; read through ffmpeg.</summary>
        DNxHR,

        /// <summary>ProRes in a MOV; all-intra, widely compatible; read through ffmpeg.</summary>
        ProRes,
    }

    public enum ProxyState
    {
        NotCached,

        /// <summary>Waiting for a build slot (see EditSharpConfig.MaxConcurrentProxyBuilds).</summary>
        Queued,

        Building,

        /// <summary>A build stopped part-way (cancelled, or the app exited). Readable up to AvailableUpTo; the next BuildAsync resumes it.</summary>
        Partial,

        Complete,

        /// <summary>The last build failed. Anything it wrote before failing stays readable up to AvailableUpTo.</summary>
        Failed,
    }

    /// <summary>
    /// Where a file's proxy stands. AvailableUpTo is how much of the original,
    /// from its start, can be read from the proxy right now; Progress is the
    /// same as a fraction of the whole.
    /// </summary>
    public readonly record struct ProxyStatus(
        ProxyState State,
        TimeSpan AvailableUpTo,
        ProxyFormat? Format,
        double Progress,
        Exception? Error)
    {
        public static ProxyStatus NotCached => new(ProxyState.NotCached, TimeSpan.Zero, null, 0, null);
    }

    public sealed class ProxyStatusChangedEventArgs(string sourcePath, ProxyStatus status) : EventArgs
    {
        public string SourcePath { get; } = sourcePath;
        public ProxyStatus Status { get; } = status;
    }

    /// <summary>
    /// One original's proxy on disk. Path is the .esrp file, or for a MOV
    /// proxy its sidecar (which lists the segments or the final file).
    /// Width/Height/FrameRate are the proxy's own.
    /// </summary>
    public sealed record ProxyEntry(string SourceHash, ProxyFormat Format, string Path, int Width, int Height, double FrameRate)
    {
        public bool IsEsrp => Format is ProxyFormat.EsrpDelta7 or ProxyFormat.EsrpRgba;
    }
}
