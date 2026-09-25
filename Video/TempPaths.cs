using EditSharp;
using System.IO;

namespace EditSharp.Video
{
    //scratch file paths under EditSharpConfig.TempDirectory; each subfolder is created on first use
    internal static class TempPaths
    {
        public static string GetVideoTempFilePath(string fileName)
        {
            string dir = Path.Combine(EditSharpConfig.TempDirectory, "Video");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName);
        }
    }
}
