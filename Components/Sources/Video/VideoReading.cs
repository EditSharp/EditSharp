using System;
using SkiaSharp;
using EditSharp.Video;

namespace EditSharp.Components.Sources.Video
{
    /// <summary>
    /// A frame handed back by a reader. Transient frames are fresh every call
    /// and the caller disposes them once drawn; non-transient frames stay owned
    /// by the reader (a cached still) and must not be disposed by the caller.
    /// </summary>
    internal readonly record struct VideoFrame(SKImage Image, bool Transient);

    internal enum VideoReadMode
    {
        /// <summary>
        /// Forward playback and rendering: full quality, content times must
        /// never decrease between calls.
        /// </summary>
        Sequential,

        /// <summary>
        /// Scrubbing and reverse playback: any time in any order, fast, and
        /// allowed to be preview fidelity (it always reads the proxy).
        /// </summary>
        RandomAccess,
    }

    /// <summary>
    /// What a whole session needs from every source it prepares. Mode is fixed
    /// for the session (see RenderSettings.SourceMode).
    /// </summary>
    internal sealed record VideoPrepareContext(HardwareAccelerator HwAccel, SourceMode Mode = SourceMode.SourceOnly);

    /// <summary>
    /// How one reader should read. StartAt is the content time of the first
    /// frame that will be asked for (a sequential reader seeks there once).
    /// Speed is only a hint for kinds that retime their own decode; the times
    /// passed to GetFrame are already content time. MaxWidth/MaxHeight is the
    /// largest size compositing can use (0 = native); kinds clamp it to their
    /// native size and may ignore it.
    ///
    /// CallerOwnsFrames asks the reader to hand over every frame it decodes
    /// (Transient) instead of keeping its current one; for a caller that
    /// holds many frames at once (a prefetch buffer) and does its own repeat
    /// handling. Frames that are shared by nature (a cached still) may still
    /// come back non-transient.
    /// </summary>
    internal sealed record VideoReaderOptions(
        VideoReadMode Mode,
        TimeSpan StartAt,
        int Fps = 30,
        double Speed = 1d,
        int MaxWidth = 0,
        int MaxHeight = 0,
        bool CallerOwnsFrames = false);

    /// <summary>A source readied for one session; opens readers on demand and owns whatever they share.</summary>
    internal interface IPreparedVideoSource : IDisposable
    {
        /// <summary>The source's full-resolution size, before any MaxWidth/MaxHeight clamp.</summary>
        (int Width, int Height) NativeSize { get; }

        IVideoFrameReader OpenReader(VideoReaderOptions options);
    }

    internal interface IVideoFrameReader : IDisposable
    {
        /// <summary>
        /// The frame at `contentTime` (time since the in-point, at 1x). Throws
        /// SourceUnavailableException when it can't; including EndOfSource.
        /// </summary>
        VideoFrame GetFrame(TimeSpan contentTime);
    }
}
