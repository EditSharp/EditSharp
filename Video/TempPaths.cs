using EditSharp;
using System.IO;

namespace EditSharp.Video
{
    /// <summary>
    /// Temp file paths, routed under EditSharpConfig.TempDirectory rather
    /// than Windows' %TEMP% — see that property's remarks for why: testing
    /// traced a native ffmpeg crash (0xc0000409, a stack buffer overrun)
    /// specifically to files living in %TEMP%, most likely a path-length
    /// issue in the "-/option" file-loading mechanism used for large filter
    /// graphs.
    ///
    /// Each subfolder is created lazily on first use rather than assumed to
    /// exist — EditSharp doesn't require the host app to set up any folder
    /// structure ahead of time. Directory.CreateDirectory is a no-op when
    /// the folder is already there, so this is cheap to call on every
    /// render.
    ///
    /// This file used to be part of GraphUtilities, alongside the ffmpeg
    /// number/argument helpers now in FfmpegArgs — the two shared nothing,
    /// so they were split apart during the EditSharp reorg.
    /// </summary>
    internal static class TempPaths
    {
        public static string GetImageTempFilePath(string fileName)
        {
            string dir = Path.Combine(EditSharpConfig.TempDirectory, "Image");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName);
        }

        public static string GetVideoTempFilePath(string fileName)
        {
            string dir = Path.Combine(EditSharpConfig.TempDirectory, "Video");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName);
        }

        /// <summary>
        /// Scratch path for the real-audio-pipeline's raw PCM temp files
        /// (AudioMixer's master buffer, on its way into Renderer.
        /// RenderAndEncodeAsync's ffmpeg mux) — same lazy-subfolder convention
        /// as Image/Video above.
        /// </summary>
        public static string GetAudioTempFilePath(string fileName)
        {
            string dir = Path.Combine(EditSharpConfig.TempDirectory, "Audio");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName);
        }
    }
}
