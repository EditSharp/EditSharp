using System.Collections.Generic;
using EditSharp.Components;
using EditSharp.Render;

namespace EditSharp.Assembly
{
    /// <summary>
    /// Shared lookup tables used across the assembly pipeline: ffmpeg's xfade
    /// transition names, and encoder names for each VideoCodec (both software
    /// and NVENC) and AudioCodec.
    /// </summary>
    internal static partial class Constants
    {
        // Maps TransitionType enum to ffmpeg's xfade transition names.
        public static readonly Dictionary<TransitionType, string> XfadeNames = new()
        {
            [TransitionType.Fade] = "fade",
            [TransitionType.FadeBlack] = "fadeblack",
            [TransitionType.FadeWhite] = "fadewhite",
            [TransitionType.FadeGrays] = "fadegrays",
            [TransitionType.FadeFast] = "fadefast",
            [TransitionType.FadeSlow] = "fadeslow",
            [TransitionType.Dissolve] = "dissolve",
            [TransitionType.Distance] = "distance",
            [TransitionType.Pixelize] = "pixelize",
            [TransitionType.WipeLeft] = "wipeleft",
            [TransitionType.WipeRight] = "wiperight",
            [TransitionType.WipeUp] = "wipeup",
            [TransitionType.WipeDown] = "wipedown",
            [TransitionType.WipeTopLeft] = "wipetl",
            [TransitionType.WipeTopRight] = "wipetr",
            [TransitionType.WipeBottomLeft] = "wipebl",
            [TransitionType.WipeBottomRight] = "wipebr",
            [TransitionType.SlideLeft] = "slideleft",
            [TransitionType.SlideRight] = "slideright",
            [TransitionType.SlideUp] = "slideup",
            [TransitionType.SlideDown] = "slidedown",
            [TransitionType.SmoothLeft] = "smoothleft",
            [TransitionType.SmoothRight] = "smoothright",
            [TransitionType.SmoothUp] = "smoothup",
            [TransitionType.SmoothDown] = "smoothdown",
            [TransitionType.CircleOpen] = "circleopen",
            [TransitionType.CircleClose] = "circleclose",
            [TransitionType.CircleCrop] = "circlecrop",
            [TransitionType.RectCrop] = "rectcrop",
            [TransitionType.Radial] = "radial",
            [TransitionType.VerticalOpen] = "vertopen",
            [TransitionType.VerticalClose] = "vertclose",
            [TransitionType.HorizontalOpen] = "horzopen",
            [TransitionType.HorizontalClose] = "horzclose",
            [TransitionType.DiagonalTopLeft] = "diagtl",
            [TransitionType.DiagonalTopRight] = "diagtr",
            [TransitionType.DiagonalBottomLeft] = "diagbl",
            [TransitionType.DiagonalBottomRight] = "diagbr",
            [TransitionType.SliceLeft] = "hlslice",
            [TransitionType.SliceRight] = "hrslice",
            [TransitionType.SliceUp] = "vuslice",
            [TransitionType.SliceDown] = "vdslice",
            [TransitionType.WindLeft] = "hlwind",
            [TransitionType.WindRight] = "hrwind",
            [TransitionType.WindUp] = "vuwind",
            [TransitionType.WindDown] = "vdwind",
            [TransitionType.CoverLeft] = "coverleft",
            [TransitionType.CoverRight] = "coverright",
            [TransitionType.CoverUp] = "coverup",
            [TransitionType.CoverDown] = "coverdown",
            [TransitionType.RevealLeft] = "revealleft",
            [TransitionType.RevealRight] = "revealright",
            [TransitionType.RevealUp] = "revealup",
            [TransitionType.RevealDown] = "revealdown",
            [TransitionType.SqueezeHorizontal] = "squeezeh",
            [TransitionType.SqueezeVertical] = "squeezev",
            [TransitionType.ZoomIn] = "zoomin",
            [TransitionType.HorizontalBlur] = "hblur",
        };

        public static readonly Dictionary<VideoCodec, string> VideoCodecNames = new()
        {
            [VideoCodec.H264] = "libx264",
            [VideoCodec.H265] = "libx265",
            [VideoCodec.AV1] = "libaom-av1",
            [VideoCodec.GIF] = "gif",
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
