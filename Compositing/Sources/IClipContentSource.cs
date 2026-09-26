using System;
using System.Collections.Generic;
using SkiaSharp;
using EditSharp.Components.Clips;
using EditSharp.Components.Nodes;
using EditSharp.Compositing.Gpu;

namespace EditSharp.Compositing.Sources
{
    /// <summary>
    /// What FrameCompositor composites against: this frame's image for every
    /// InputNode of a clip, keyed by node Id, each flagged with whether the
    /// caller disposes it (Transient) or it stays owned by the source.
    /// `frameIndex` is the timeline frame being composed (buffered sources
    /// key their prefetch on it); `contentTime` is that frame's content time;
    /// `graph` is the clip's graph snapshot for this frame.
    /// </summary>
    internal interface IClipContentSource
    {
        IReadOnlyDictionary<Guid, (SKImage Image, bool Transient)> GetContent(
            VideoClip clip, Graph graph, Time contentTime, int frameIndex, int canvasWidth, int canvasHeight, SurfacePool pool);
    }
}
