using System;
using System.Collections.Generic;
using System.Text;

namespace EditSharp.Render
{
    public enum VideoCodec
    {
        H264,
        H265,
        AV1,
        GIF,
        FFV1,
    }

    public enum AudioCodec
    {
        AAC,
        MP3,
        FLAC
    }

    /// <summary>
    /// A single top-level switch covering every stage of the pipeline that
    /// has a hardware path at all: source DECODE (SkSourceDecoder's ffmpeg
    /// subprocesses), in-process COMPOSITE (the Skia GRContext the
    /// compositor's surfaces are backed by, see GpuContext/SkSurfacePool),
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
