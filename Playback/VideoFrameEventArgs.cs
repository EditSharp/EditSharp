using System;

namespace EditSharp.Playback
{
    /// <summary>One rendered frame: RGBA8888 with straight alpha and no row padding.</summary>
    /// <remarks><see cref="Buffer"/> is rented from <c>ArrayPool&lt;byte&gt;.Shared</c> and returned when the handler returns, so copy <c>Buffer[..Length]</c> before then to keep the pixels.</remarks>
    public sealed class VideoFrameEventArgs : EventArgs
    {
        /// <summary>The pixels; valid only during the handler.</summary>
        public byte[] Buffer { get; }

        /// <summary>How many bytes of <see cref="Buffer"/> hold pixels.</summary>
        public int Length { get; }

        /// <summary>The frame's width in pixels.</summary>
        public int Width { get; }

        /// <summary>The frame's height in pixels.</summary>
        public int Height { get; }

        /// <summary>The frame's timeline time.</summary>
        public Time Position { get; }

        /// <summary>Wraps one frame.</summary>
        /// <param name="buffer">The pixels.</param>
        /// <param name="length">How many bytes of <paramref name="buffer"/> hold pixels.</param>
        /// <param name="width">The width in pixels.</param>
        /// <param name="height">The height in pixels.</param>
        /// <param name="position">The frame's timeline time.</param>
        public VideoFrameEventArgs(byte[] buffer, int length, int width, int height, Time position)
        {
            Buffer = buffer;
            Length = length;
            Width = width;
            Height = height;
            Position = position;
        }
    }
}
