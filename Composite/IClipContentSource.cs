using System;
using System.Collections.Generic;
using SkiaSharp;
using EditSharp.Components.Clips;

namespace EditSharp.Composite
{
    /// <summary>
    /// Shared surface SkFrameCompositor composites against. Two very
    /// different implementations satisfy it:
    ///   - SkClipContentSource: a persistent, forward-only decode (one
    ///     long-lived ffmpeg pipe per Video-type VideoSourceNode) — used by
    ///     Render and by Playback's normal forward playback loop.
    ///   - ScrubFrameSource: a one-shot, arbitrary-position I-frame decode
    ///     with no persistent pipe — used by Playback.ScrubToAsync and
    ///     reverse playback, where "jump to any position instantly" is the
    ///     whole point and a forward-only pipe cannot do that.
    ///
    /// Kept as a plain interface rather than a shared base class — the two
    /// implementations share almost nothing beyond this one call shape (see
    /// each class's own remarks).
    /// </summary>
    internal interface IClipContentSource
    {
        IReadOnlyDictionary<Guid, (SKImage Image, bool Transient)> GetContent(
            VideoClip clip, double clipSeconds, int canvasWidth, int canvasHeight, SkSurfacePool pool);
    }
}