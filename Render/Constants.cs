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

        // Hardware encoder candidates per codec, in TRY-FIRST-TO-LAST priority
        // order. GetVideoEncoderSettingsAsync probes each in turn with a
        // trial encode and uses the first that actually works on this
        // machine — vendor availability varies (NVIDIA/AMD/Intel), so this
        // is a preference order, not an assumption any specific one exists.
        // GIF has no hardware equivalent at all — it always encodes on the
        // CPU regardless of HardwareAccelerator, since there's no such thing
        // as a hardware GIF encoder.
        //
        // NOT VERIFIED against this project's actual installed ffmpeg build
        // — h264_nvenc/hevc_nvenc are known-good from the pre-existing
        // NvencCodecNames table this replaces, but av1_amf/h264_qsv/etc.
        // names are written from general ffmpeg encoder-naming convention,
        // not confirmed here. GetVideoEncoderSettingsAsync's own trial-encode
        // probe is exactly the safety net for that — an unavailable or
        // misnamed encoder just fails its probe and falls through to the
        // next candidate, same as any other legitimately-unsupported one.
        public static readonly Dictionary<VideoCodec, string[]> HardwareCodecNames = new()
        {
            [VideoCodec.H264] = ["h264_nvenc", "h264_amf", "h264_qsv"],
            [VideoCodec.H265] = ["hevc_nvenc", "hevc_amf", "hevc_qsv"],
            [VideoCodec.AV1] = ["av1_nvenc", "av1_amf", "av1_qsv"], // av1_nvenc needs an RTX 40-series+ GPU
        };

        // ffmpeg -hwaccel candidates for source DECODE, in TRY-FIRST-TO-LAST
        // priority order: vendor-specific first (fastest, needs a matching
        // GPU), d3d11va as a broad Windows fallback that works across
        // vendors via DXVA, then no -hwaccel flag at all (plain software
        // decode) as the final, always-available fallback — represented as
        // null rather than a string since it's "omit the flag", not a flag
        // value. GetDecodeHwAccelArgsAsync probes each against the actual
        // source being decoded, same trial-and-fallback shape as encode.
        public static readonly string?[] DecodeHwAccelCandidates =
        [
            "cuda",
            "d3d11va",
            "vulkan",
            null,
        ];

        public static readonly Dictionary<AudioCodec, string> AudioCodecNames = new()
        {
            [AudioCodec.AAC] = "aac",
            [AudioCodec.MP3] = "libmp3lame",
            [AudioCodec.FLAC] = "flac",
        };
    }
}
