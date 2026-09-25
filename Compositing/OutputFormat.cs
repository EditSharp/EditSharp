namespace EditSharp.Compositing
{
    //the pixel format frames are composited in and handed to ffmpeg: 8-bit RGBA
    internal static class OutputFormat
    {
        public const string FfmpegPixelFormat = "rgba";
        public const int BytesPerPixel = 4;
    }
}
