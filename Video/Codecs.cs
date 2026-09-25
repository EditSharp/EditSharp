using System;
using System.Collections.Generic;
using System.Text;
 
namespace EditSharp.Video
{
    public enum VideoCodec
    {
        H264,
        H265,
        AV1,
        GIF,
        FFV1,
 
        /// <summary>
        /// Avid DNxHR — one of two codecs OptimizedMediaCache can build its
        /// persistent optimized media with (see EditSharpConfig.
        /// OptimizedMediaCodec), and the DEFAULT of the two: an open format
        /// with no licensing friction, and ffmpeg's "dnxhd" encoder handles
        /// it natively and cross-platform. All-intra, which is the entire
        /// point for this use — see OptimizedMediaCache's class remarks.
        /// CPU encode/decode only; no vendor has a hardware codec block for
        /// this format — decided in conversation, see OptimizedMediaCache's
        /// remarks on Vulkan Video's actual codec coverage (H.264/HEVC/AV1
        /// only, nothing else, on any vendor).
        /// </summary>
        DNxHR,
 
        /// <summary>
        /// Apple ProRes — the other codec OptimizedMediaCache can build
        /// optimized media with. Common in pro NLE ecosystems; ffmpeg's
        /// "prores_ks" encoder (not the older, lower-quality "prores") is
        /// what this project uses for it. Same all-intra, CPU-only
        /// reasoning as DNxHR — see its own remarks.
        /// </summary>
        ProRes,
    }
 
    public enum AudioCodec
    {
        AAC,
        MP3,
        FLAC
    }
 
    /// <summary>
    /// A single top-level switch covering every stage of the pipeline that
    /// has a hardware path at all: source DECODE (SourceDecoder's ffmpeg
    /// subprocesses), in-process COMPOSITE (the Skia GRContext the
    /// compositor's surfaces are backed by, see GpuContext/SurfacePool),
    /// and final ENCODE (FfmpegRunner's mux/encode step). Replaces the old
    /// Nvenc-only enum, which named one specific vendor encoder rather than
    /// describing an intent — this one says "use hardware wherever it's
    /// available" and leaves resolving that to the fastest option ACTUALLY
    /// present on the machine at each of the three stages independently.
    ///
    /// None forces software at every single stage, deliberately and
    /// unconditionally — not "prefer software", an absolute guarantee, since
    /// this is the value a consumer reaches for specifically to get
    /// deterministic, hardware-independent output (e.g. matching a
    /// reference render, or working around a suspect driver).
    ///
    /// GPU attempts hardware at each stage independently, probes it before
    /// committing, and falls back to software FOR THAT STAGE ONLY if the
    /// probe fails — decided in conversation: a machine with working NVENC
    /// but no GPU decode support shouldn't lose GPU encoding just because
    /// decode fell back. Every fallback is logged loudly via
    /// EditSharpConfig.Logger.Log (not LogVerbose) specifically so a sudden,
    /// unexplained slowdown on GPU is never silent — see FfmpegRunner and
    /// GpuContext for where each stage's probe and fallback actually happen.
    /// </summary>
    public enum HardwareAccelerator
    {
        None,
        GPU,
    }
}
 