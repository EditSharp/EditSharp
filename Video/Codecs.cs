using System;
using System.Collections.Generic;
using System.Text;

namespace EditSharp.Video
{
    /// <summary>The video codec a render encodes with.</summary>
    public enum VideoCodec
    {
        /// <summary>H.264 (AVC); hardware encoders are tried first when the GPU is allowed.</summary>
        H264,

        /// <summary>H.265 (HEVC); hardware encoders are tried first when the GPU is allowed.</summary>
        H265,

        /// <summary>AV1; hardware encoders are tried first when the GPU is allowed.</summary>
        AV1,

        /// <summary>An animated GIF, with no audio; always encoded on the CPU.</summary>
        GIF,

        /// <summary>FFV1, lossless; always encoded on the CPU.</summary>
        FFV1,

        /// <summary>Avid DNxHR: every frame a keyframe, open, and read by most editors; always encoded on the CPU.</summary>
        DNxHR,

        /// <summary>Apple ProRes, through ffmpeg's prores_ks encoder: every frame a keyframe; always encoded on the CPU.</summary>
        ProRes,
    }

    /// <summary>The audio codec a render encodes with.</summary>
    public enum AudioCodec
    {
        /// <summary>AAC at 192 kb/s.</summary>
        AAC,

        /// <summary>MP3 through LAME, at 192 kb/s.</summary>
        MP3,

        /// <summary>FLAC, lossless.</summary>
        FLAC
    }

    /// <summary>Whether decoding, compositing and encoding may use the GPU.</summary>
    public enum HardwareAccelerator
    {
        /// <summary>Software at every stage, for output that doesn't depend on the machine's GPU or driver.</summary>
        None,

        /// <summary>
        /// The GPU at each stage where it works. Each stage (decode, composite,
        /// encode) is probed on its own and falls back to software alone if its
        /// probe fails; every fallback is logged.
        /// </summary>
        GPU,
    }
}
