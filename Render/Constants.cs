using System.Collections.Generic;
using EditSharp.Components;

namespace EditSharp.Render
{
    /// <summary>
    /// Shared lookup tables used across the assembly pipeline: encoder names
    /// for each VideoCodec (both software and NVENC) and AudioCodec.
    ///
    /// XfadeNames (TransitionType -> ffmpeg xfade name) removed as part of
    /// the Skia compositor migration's item 8 — Transition is now an
    /// abstract class (FadeTransition/FadeToColorTransition/SlideTransition,
    /// see Transition.cs) with no TransitionType enum to key a dictionary
    /// on. Nothing needs an ffmpeg xfade name string anymore for any
    /// transition that has a Skia implementation.
    /// </summary>
    internal static partial class Constants
    {
        public static readonly Dictionary<VideoCodec, string> VideoCodecNames = new()
        {
            [VideoCodec.H264] = "libx264",
            [VideoCodec.H265] = "libx265",
            [VideoCodec.AV1] = "libaom-av1",
            [VideoCodec.GIF] = "gif",
            [VideoCodec.FFV1] = "ffv1",
        };

        // NVENC uses different encoder names than the software libraries above.
        // GIF has no NVENC equivalent — it always encodes on the CPU regardless of
        // HardwareAccelerator, since there's no such thing as a hardware GIF encoder.
        public static readonly Dictionary<VideoCodec, string> NvencCodecNames = new()
        {
            [VideoCodec.H264] = "h264_nvenc",
            [VideoCodec.H265] = "hevc_nvenc",
            [VideoCodec.AV1] = "av1_nvenc", // requires an RTX 40-series or newer GPU
        };

        public static readonly Dictionary<AudioCodec, string> AudioCodecNames = new()
        {
            [AudioCodec.AAC] = "aac",
            [AudioCodec.MP3] = "libmp3lame",
            [AudioCodec.FLAC] = "flac",
        };
    }
}
