using System;

namespace EditSharp.Caching.Proxy
{
    /// <summary>How a proxy is stored on disk.</summary>
    public enum ProxyFormat
    {
        /// <summary>.esrp, 7-bit indexed against one shared palette. Smallest, with the lowest colour fidelity; read without ffmpeg.</summary>
        EsrpDelta7,

        /// <summary>.esrp, full RGBA8888. Large, with full fidelity at proxy size; read without ffmpeg.</summary>
        EsrpRgba,

        /// <summary>DNxHR in a MOV: every frame a keyframe, and readable by most editors; read through ffmpeg.</summary>
        DNxHR,

        /// <summary>ProRes in a MOV: every frame a keyframe, and readable by most editors; read through ffmpeg.</summary>
        ProRes,
    }

    /// <summary>Where a file's proxy is in its life.</summary>
    public enum ProxyState
    {
        /// <summary>No proxy exists and none is queued.</summary>
        NotCached,

        /// <summary>Waiting for a build slot (see <see cref="EditSharpConfig.MaxConcurrentProxyBuilds"/>).</summary>
        Queued,

        /// <summary>Being built; readable up to <see cref="ProxyStatus.AvailableUpTo"/>.</summary>
        Building,

        /// <summary>A build stopped part-way (cancelled, or the app exited). Readable up to <see cref="ProxyStatus.AvailableUpTo"/>; the next build resumes it.</summary>
        Partial,

        /// <summary>Built in full.</summary>
        Complete,

        /// <summary>The last build failed. Anything it wrote before failing stays readable up to <see cref="ProxyStatus.AvailableUpTo"/>.</summary>
        Failed,
    }

    /// <summary>Where a file's proxy stands.</summary>
    /// <param name="State">The proxy's state.</param>
    /// <param name="AvailableUpTo">How much of the original, from its start, can be read from the proxy right now.</param>
    /// <param name="Format">The proxy's format; null when there is none.</param>
    /// <param name="Progress"><paramref name="AvailableUpTo"/> as a fraction of the original's length, 0 to 1.</param>
    /// <param name="Error">Why the last build failed; null unless <paramref name="State"/> is Failed.</param>
    public readonly record struct ProxyStatus(
        ProxyState State,
        TimeSpan AvailableUpTo,
        ProxyFormat? Format,
        double Progress,
        Exception? Error)
    {
        /// <summary>The status of a file with no proxy.</summary>
        public static ProxyStatus NotCached => new(ProxyState.NotCached, TimeSpan.Zero, null, 0, null);
    }

    /// <summary>Details of a proxy status change.</summary>
    /// <param name="sourcePath">The original file whose proxy changed.</param>
    /// <param name="status">Its new status.</param>
    public sealed class ProxyStatusChangedEventArgs(string sourcePath, ProxyStatus status) : EventArgs
    {
        /// <summary>The original file whose proxy changed.</summary>
        public string SourcePath { get; } = sourcePath;

        /// <summary>Its new status.</summary>
        public ProxyStatus Status { get; } = status;
    }

    /// <summary>One original's proxy on disk.</summary>
    /// <param name="SourceHash">The hash identifying the original file's content.</param>
    /// <param name="Format">How the proxy is stored.</param>
    /// <param name="Path">The .esrp file, or for a MOV proxy its sidecar listing the segments or the final file.</param>
    /// <param name="Width">The proxy's width in pixels.</param>
    /// <param name="Height">The proxy's height in pixels.</param>
    /// <param name="FrameRate">The proxy's frames per second.</param>
    public sealed record ProxyEntry(string SourceHash, ProxyFormat Format, string Path, int Width, int Height, double FrameRate)
    {
        /// <summary>Whether the proxy is an .esrp file.</summary>
        public bool IsEsrp => Format is ProxyFormat.EsrpDelta7 or ProxyFormat.EsrpRgba;
    }
}
