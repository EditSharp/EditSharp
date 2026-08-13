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

    public enum HardwareAccelerator
    {
        None,
        Nvenc,
    }

    /// <summary>
    /// Whether source FILES are decoded on the GPU. Entirely separate from
    /// HardwareAccelerator, which selects the ENCODER — the two ends of the
    /// pipeline are independent, and keeping them on separate knobs is what makes
    /// it possible to measure either one on its own.
    ///
    /// Decode only. No -hwaccel_output_format is ever requested, so decoded frames
    /// land back in system memory and the filter graph is untouched: every filter
    /// in this pipeline (perspective, alphaextract, fillborders, blend, xfade)
    /// stays exactly where it is, running exactly as it does now. That is the
    /// whole reason this is a safe change — it buys back decode time without
    /// paying for hwupload/hwdownload round trips around the software chain.
    /// </summary>
    public enum HardwareDecoder
    {
        //software decode, as before
        None,

        //`-hwaccel auto`: ffmpeg picks whatever the machine actually offers and
        //silently falls back to software when it offers nothing. Verified against
        //a machine with no GPU at all: exit 0, and the decoded output is
        //byte-identical to a plain software decode. Cheap to leave on, but it does
        //not say WHICH method it chose, so prefer an explicit value when measuring
        Auto,

        //`-hwaccel cuda` (NVDEC). Explicit and therefore measurable, but it does
        //NOT fall back on its own — verified: on a machine with no CUDA driver the
        //whole run dies with exit 255, "No device available for decoder". So this
        //value is probed before use and quietly downgraded to software, the same
        //way HardwareAccelerator.Nvenc is
        Cuda,
    }
}
