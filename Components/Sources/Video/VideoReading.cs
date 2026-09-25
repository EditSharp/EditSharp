using System;
using SkiaSharp;
using EditSharp.Compositing.Gpu;
using EditSharp.Compositing.Sources;
using EditSharp.Video;

namespace EditSharp.Components.Sources.Video
{
    /// <summary>A frame handed back by a reader.</summary>
    /// <param name="Image">The picture.</param>
    /// <param name="Transient">True when the frame is new each call and the caller disposes it once drawn; false when the reader keeps it (a cached still) and the caller must not dispose it.</param>
    internal readonly record struct VideoFrame(SKImage Image, bool Transient);

    /// <summary>How a reader is going to be asked for frames.</summary>
    internal enum VideoReadMode
    {
        /// <summary>Forward playback and rendering: full quality, and content times never go backwards between calls.</summary>
        Sequential,

        /// <summary>Scrubbing and reverse playback: any time in any order, fast, at preview quality (it always reads the proxy).</summary>
        RandomAccess,
    }

    /// <summary>What a session needs from every source it prepares.</summary>
    /// <param name="HwAccel">The hardware decoder to use where one fits.</param>
    /// <param name="Mode">Proxy or original, fixed for the session (see RenderSettings.SourceMode).</param>
    internal sealed record VideoPrepareContext(HardwareAccelerator HwAccel, SourceMode Mode = SourceMode.SourceOnly);

    /// <summary>How one reader should read.</summary>
    /// <param name="Mode">How the reader will be asked for frames.</param>
    /// <param name="StartAt">The content time of the first frame that will be asked for; a sequential reader seeks there once.</param>
    /// <param name="Fps">The session's frame rate.</param>
    /// <param name="Speed">A hint for kinds that retime their own decode; the times passed to GetFrame are already content time.</param>
    /// <param name="MaxWidth">The largest width compositing can use; 0 for native. Kinds clamp it to their native size, and may ignore it.</param>
    /// <param name="MaxHeight">The largest height compositing can use; 0 for native.</param>
    /// <param name="CallerOwnsFrames">Hand over every decoded frame as transient instead of keeping the current one, for a caller that holds many frames at once (a prefetch buffer). Frames shared by nature (a cached still) can still come back non-transient.</param>
    /// <param name="CanvasWidth">The output canvas width, for kinds that draw at canvas size (generators); 0 if unknown.</param>
    /// <param name="CanvasHeight">The output canvas height; 0 if unknown.</param>
    /// <param name="Compositor">What a compositor-bound reader may use; set only while compositing.</param>
    internal sealed record VideoReaderOptions(
        VideoReadMode Mode,
        TimeSpan StartAt,
        int Fps = 30,
        double Speed = 1d,
        int MaxWidth = 0,
        int MaxHeight = 0,
        bool CallerOwnsFrames = false,
        int CanvasWidth = 0,
        int CanvasHeight = 0,
        CompositorAccess? Compositor = null);

    /// <summary>What a compositor-bound reader (see <see cref="ICompositorBound"/>) may use.</summary>
    /// <param name="Pool">The compositor's surface pool, used on its GPU thread.</param>
    /// <param name="Options">The session's content options, for anything the reader composites itself (a nested timeline).</param>
    internal sealed record CompositorAccess(SurfacePool Pool, ContentSourceOptions Options);

    /// <summary>A prepared source whose readers draw with the compositor's own GPU context.</summary>
    /// <remarks>Compositing never buffers these: they're opened and read inside the frame's composite, on the GPU thread, with <see cref="VideoReaderOptions.Compositor"/> set.</remarks>
    internal interface ICompositorBound { }

    /// <summary>A source readied for one session; opens readers on demand and owns whatever they share.</summary>
    internal interface IPreparedVideoSource : IDisposable
    {
        /// <summary>The source's full-resolution size, before any MaxWidth/MaxHeight clamp; (0, 0) for kinds drawn at canvas size.</summary>
        (int Width, int Height) NativeSize { get; }

        /// <summary>Opens a reader.</summary>
        /// <param name="options">How the reader should read.</param>
        /// <returns>The reader; the caller disposes it.</returns>
        /// <exception cref="SourceUnavailableException">The reader can't be opened.</exception>
        IVideoFrameReader OpenReader(VideoReaderOptions options);
    }

    /// <summary>Reads frames from a prepared source.</summary>
    internal interface IVideoFrameReader : IDisposable
    {
        /// <summary>The frame at a content time.</summary>
        /// <param name="contentTime">Time since the in-point, at 1x.</param>
        /// <returns>The frame; see <see cref="VideoFrame.Transient"/> for who disposes it.</returns>
        /// <exception cref="SourceUnavailableException">The frame can't be read, including past the end (<see cref="SourceUnavailableReason.EndOfSource"/>).</exception>
        VideoFrame GetFrame(TimeSpan contentTime);
    }
}
