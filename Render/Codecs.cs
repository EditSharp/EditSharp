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
    /// Which render pipeline builds each frame, and which encoder finishes the
    /// output. This now does double duty: it still selects the final encoder
    /// (NVENC vs software), and it ALSO selects the per-frame filter-chain
    /// construction strategy — CPU stock filters (perspective, alphamerge,
    /// colorchannelmixer, fillborders, ...) for None, the libplacebo GPU shader
    /// chain for Nvenc. See FrameClipVideoChain / the render-mode abstraction
    /// in FrameRenderer.
    ///
    /// HardwareDecoder used to be a separate, independent knob (decode only,
    /// entirely orthogonal to which encoder or filter chain was in play). It
    /// no longer has a meaning: the frame-by-frame render reads already-decoded
    /// optimized media (see OptimizedMediaBuilder), so there is no per-render
    /// source decode step left for it to apply to. Removed rather than kept
    /// around unused.
    /// </summary>
    public enum HardwareAccelerator
    {
        None,
        Nvenc,
    }
}
