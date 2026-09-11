using System.Collections.Generic;
using EditSharp.Components;
 
namespace EditSharp.Video
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
    internal static partial class CodecNames
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
        // priority order: cuda (NVIDIA, GPU-scale-capable — decode AND the
        // resize step stay on the GPU, only the final already-small frame
        // gets downloaded for the pipe), THEN d3d11va as a broad Windows
        // decode-only fallback (no GPU scale filter for this candidate, so
        // it gets hardware DECODE but CPU scale — a real, deliberate
        // asymmetry, not an oversight), then plain software as the final
        // fallback (ScaleFilter null, HwaccelOutputFormat null).
        //
        // "vulkan"/scale_vulkan WAS in this list, ranked between cuda and
        // d3d11va, and has been REMOVED — found in the field, not
        // theoretical: on Intel iGPUs, ffmpeg's vulkan hwaccel decode +
        // scale_vulkan + hwdownload,format=nv12 chain reliably PASSES
        // ProbeDecodePlanAsync's probe (the process exits 0 — the mechanism
        // is present and "works") while silently producing chroma-plane-
        // misaligned frames, which show up as exactly the top/bottom
        // discolored-band corruption reported for real video content while
        // procedural (non-decoded) content is unaffected. An exit-code-only
        // probe can't catch this — it confirms the filter chain RUNS, not
        // that its output is pixel-correct — so rather than add a pixel-
        // level self-check to the probe, the safer fix is to stop offering
        // this specific candidate at all: cuda (NVIDIA) still gets full
        // GPU decode+scale, and every other vendor (Intel, AMD, and any
        // NVIDIA machine where cuda itself isn't available) now lands on
        // d3d11va's known-good decode-only path instead of a GPU-scale path
        // that can pass its own probe while still being wrong. Revisit only
        // once a future ffmpeg/driver combination is confirmed correct here
        // (ideally via an actual pixel comparison, not just an exit code).
        //
        // NOTE: none of these candidates ever apply to OptimizedMediaCache's
        // own output (DNxHR/ProRes) — that decode is always forced to
        // DecodeHwAccelPlan.Software regardless of this table, since no
        // candidate here (or anywhere else) has a hardware decode path for
        // either codec. See ContentPreparation.ProbeVideoAsync.
        public static readonly (string Candidate, string? HwaccelOutputFormat, string? ScaleFilter)[]
            DecodeHwAccelCandidates =
        [
            ("cuda", "cuda", "scale_cuda"),
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
 