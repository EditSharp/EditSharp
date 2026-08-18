using System;

namespace EditSharp.Playback
{
    /// <summary>
    /// One rendered output frame, raw RGBA8888 (SkOutputFormat), straight
    /// alpha, no row padding — the exact same byte layout Renderer's
    /// accumulator writes.
    ///
    /// Buffer OWNERSHIP: Buffer is rented from ArrayPool&lt;byte&gt;.Shared
    /// and is only valid for the duration of the VideoFrame event handler.
    /// It is returned to the pool immediately after the handler returns, so
    /// a subscriber that needs the pixels past that point (uploading to a
    /// GPU texture on another thread, queuing for display, etc.) MUST copy
    /// out of Buffer[..Length] synchronously, before returning from the
    /// handler.
    /// </summary>
    public sealed class VideoFrameEventArgs : EventArgs
    {
        public byte[] Buffer { get; }
        public int Length { get; }
        public int Width { get; }
        public int Height { get; }
        public TimeSpan Position { get; }

        public VideoFrameEventArgs(byte[] buffer, int length, int width, int height, TimeSpan position)
        {
            Buffer = buffer;
            Length = length;
            Width = width;
            Height = height;
            Position = position;
        }
    }
}
