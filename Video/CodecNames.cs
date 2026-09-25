using System.Collections.Generic;
using EditSharp.Components;

namespace EditSharp.Video
{
    //ffmpeg encoder and hardware decoder names for each codec
    internal static partial class CodecNames
    {
        public static readonly Dictionary<VideoCodec, string> VideoCodecNames = new()
        {
            [VideoCodec.H264] = "libx264",
            [VideoCodec.H265] = "libx265",
            [VideoCodec.AV1] = "libaom-av1",
            [VideoCodec.GIF] = "gif",
            [VideoCodec.FFV1] = "ffv1",
            [VideoCodec.DNxHR] = "dnxhd", // handles modern DNxHR profiles too, not just legacy DNxHD
            [VideoCodec.ProRes] = "prores_ks", // the better-quality ProRes encoder, not the older "prores"
        };

        //hardware encoders to try, first to last; the first that passes a trial encode is used.
        //GIF, FFV1, DNxHR and ProRes have no hardware encoders
        public static readonly Dictionary<VideoCodec, string[]> HardwareCodecNames = new()
        {
            [VideoCodec.H264] = ["h264_nvenc", "h264_amf", "h264_qsv"],
            [VideoCodec.H265] = ["hevc_nvenc", "hevc_amf", "hevc_qsv"],
            [VideoCodec.AV1] = ["av1_nvenc", "av1_amf", "av1_qsv"], // av1_nvenc needs an RTX 40-series or newer
        };

        //hardware decoders to try, first to last, before software: cuda decodes and scales on the GPU;
        //d3d11va only decodes, and scales on the CPU. vulkan isn't offered: on Intel iGPUs its
        //decode and scale passed the probe while producing frames with misaligned chroma
        public static readonly (string Candidate, string? HwaccelOutputFormat, string? ScaleFilter)[]
            DecodeHwAccelCandidates =
        [
            ("cuda", "cuda", "scale_cuda"),
            ("d3d11va", null, null),
        ];

        public static readonly Dictionary<AudioCodec, string> AudioCodecNames = new()
        {
            [AudioCodec.AAC] = "aac",
            [AudioCodec.MP3] = "libmp3lame",
            [AudioCodec.FLAC] = "flac",
        };
    }
}
