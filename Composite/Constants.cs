using System.Collections.Generic;
using EditSharp.Components;
 
namespace EditSharp.Composite
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
 
            // Both added for OptimizedMediaCache — see that class's own
            // remarks for why these two specifically, and Codecs.cs for why
            // each is CPU-only with no hardware encoder entry in
            // HardwareCodecNames below (neither has ever had one, on any
            // vendor).
            [VideoCodec.DNxHR] = "dnxhd", // handles modern DNxHR profiles too, not just legacy DNxHD
            [VideoCodec.ProRes] = "prores_ks", // the modern, better-quality ProRes encoder, not the older "prores"
        };
 
        // Hardware encoder candidates per codec, in TRY-FIRST-TO-LAST priority
        // order. GetVideoEncoderSettingsAsync probes each in turn with a
        // trial encode and uses the first that actually works on this
        // machine — vendor availability varies (NVIDIA/AMD/Intel), so this
        // is a preference order, not an assumption any specific one exists.
        // GIF has no hardware equivalent at all — it always encodes on the
        // CPU regardless of HardwareAccelerator, since there's no such thing
        // as a hardware GIF encoder. DNxHR/ProRes are the same way, for the
        // same underlying reason (no vendor has ever exposed a hardware
        // codec block for either) — deliberately absent from this table
        // rather than an oversight; see OptimizedMediaCache's class remarks.
        public static readonly Dictionary<VideoCodec, string[]> HardwareCodecNames = new()
        {
            [VideoCodec.H264] = ["h264_nvenc", "h264_amf", "h264_qsv"],
            [VideoCodec.H265] = ["hevc_nvenc", "hevc_amf", "hevc_qsv"],
            [VideoCodec.AV1] = ["av1_nvenc", "av1_amf", "av1_qsv"], // av1_nvenc needs an RTX 40-series+ GPU
        };
 
        // ffmpeg -hwaccel candidates for source DECODE, in TRY-FIRST-TO-LAST
        // priority order: cuda then vulkan, both GPU-scale-capable (decode AND
        // the resize step stay on the GPU, only the final already-small frame
        // gets downloaded for the pipe), THEN d3d11va as a broad Windows
        // decode-only fallback (no widely available D3D11VA GPU scale filter
        // in stock ffmpeg, so this one gets hardware DECODE but CPU scale — a
        // real, deliberate asymmetry, not an oversight, and specifically why
        // it ranks below both GPU-scale candidates rather than above them),
        // then plain software as the final fallback (ScaleFilter null,
        // HwaccelOutputFormat null).
        //
        // scale_cuda and scale_vulkan are the two GPU-scale filters actually
        // exercised — NOT verified against this project's actual installed
        // ffmpeg build (same honesty flag as HardwareCodecNames' encoder
        // names). GetDecodePlanAsync's probe runs the REAL intended filter
        // chain (hwaccel + hwaccel_output_format + the scale filter itself +
        // hwdownload), not just bare `-hwaccel`, specifically because
        // encoder/filter availability can fail independently of basic decode
        // working — same lesson FfmpegRunner's own encode-side quality-arg
        // bug already taught once this pass (a probe that doesn't exercise
        // the REAL pipeline can pass while the real pipeline still breaks).
        //
        // NOTE: none of these candidates ever apply to OptimizedMediaCache's
        // own output (DNxHR/ProRes) — that decode is always forced to
        // DecodeHwAccelPlan.Software regardless of this table, since no
        // candidate here (or anywhere else) has a hardware decode path for
        // either codec. See RenderContentPreparation.ProbeVideoAsync.
        public static readonly (string Candidate, string? HwaccelOutputFormat, string? ScaleFilter)[]
            DecodeHwAccelCandidates =
        [
            ("cuda", "cuda", "scale_cuda"),
            ("vulkan", "vulkan", "scale_vulkan"),
            ("d3d11va", null, null), // decode-only — CPU scale/format-convert fallback for this candidate specifically
        ];
 
        public static readonly Dictionary<AudioCodec, string> AudioCodecNames = new()
        {
            [AudioCodec.AAC] = "aac",
            [AudioCodec.MP3] = "libmp3lame",
            [AudioCodec.FLAC] = "flac",
        };
    }
}
 