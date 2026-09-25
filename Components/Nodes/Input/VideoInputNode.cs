using EditSharp.Components.Media;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace EditSharp.Components.Nodes.Input
{
    /// <summary>An input that brings frames into a video graph.</summary>
    /// <remarks>
    /// Output: Image. Reading has two steps. PrepareAsync does the slow work once
    /// per session (probing, choosing a file and decoder) and returns a prepared
    /// handle; the handle opens cheap, synchronous readers whenever compositing
    /// needs one, in content time. The handle and its readers hold all the state,
    /// so the node stays plain data.
    /// </remarks>
    public abstract class VideoInputNode : InputNode
    {
        private static readonly NodePort[] StaticPorts = [new("Image", PortType.Image, PortDirection.Output)];

        /// <inheritdoc/>
        public override IReadOnlyList<NodePort> Ports => StaticPorts;

        /// <summary>Readies the input for one session.</summary>
        /// <param name="context">What the session needs from every input it prepares.</param>
        /// <param name="ct">Cancels preparing.</param>
        /// <returns>The prepared input, whose readers take content time; the caller disposes it.</returns>
        /// <exception cref="SourceUnavailableException">The input can't provide content.</exception>
        internal abstract Task<IPreparedVideoSource> PrepareAsync(VideoPrepareContext context, CancellationToken ct = default);

        /// <summary>One frame, for callers that need a single picture, such as thumbnails; nothing stays open afterwards.</summary>
        /// <param name="contentTime">Time since the in-point, at 1x.</param>
        /// <param name="mode">Whether to read the proxy or the original, as a session would.</param>
        /// <param name="maxWidth">The largest width wanted; 0 for the native width.</param>
        /// <param name="maxHeight">The largest height wanted; 0 for the native height.</param>
        /// <param name="ct">Cancels reading the frame.</param>
        /// <returns>The frame; the caller disposes it.</returns>
        /// <exception cref="SourceUnavailableException">The frame can't be read; with <see cref="SourceMode.ProxiesOnly"/>, a frame with no proxy is <see cref="SourceUnavailableReason.ProxyPending"/> or <see cref="SourceUnavailableReason.ProxyMissing"/>.</exception>
        public virtual Task<SKImage> GetFrameAtAsync(
            TimeSpan contentTime, SourceMode mode = SourceMode.SourceOnly, int maxWidth = 0, int maxHeight = 0,
            CancellationToken ct = default) => VideoFrames.ReadOnceAsync(PrepareAsync, contentTime, mode, maxWidth, maxHeight, ct);
    }
}
