namespace EditSharp.Compositing
{
    /// <summary>
    /// The pixel format the accumulator and the final ffmpeg mux/encode step
    /// both agree on. 8-bit RGBA for the whole Skia compositor.
    /// </summary>
    internal static class OutputFormat
    {
        public const string FfmpegPixelFormat = "rgba";
        public const int BytesPerPixel = 4;
    }
}
