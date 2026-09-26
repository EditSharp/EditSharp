using System.Collections.Generic;

namespace EditSharp.Video
{
    /// <summary>How one source is decoded: which -hwaccel, if any, and whether scaling runs on the GPU.</summary>
    /// <remarks>
    /// FfmpegRunner's probe and SourceDecoder both build their filter graph with
    /// <see cref="BuildFilterGraph"/>, so what the probe tests is exactly what the
    /// decode runs. A GPU-scaled frame is still downloaded to host memory for the
    /// pipe, but only after scaling, so the download is proxy-sized.
    /// </remarks>
    internal sealed class DecodeHwAccelPlan
    {
        //no hwaccel and no GPU scale; always works
        public static readonly DecodeHwAccelPlan Software = new("software", null, null);

        public string Candidate { get; }
        public string? HwaccelOutputFormat { get; }
        public string? ScaleFilterName { get; }

        public bool UsesGpuScale => ScaleFilterName != null;

        public DecodeHwAccelPlan(string candidate, string? hwaccelOutputFormat, string? scaleFilterName)
        {
            Candidate = candidate;
            HwaccelOutputFormat = hwaccelOutputFormat;
            ScaleFilterName = scaleFilterName;
        }

        //-hwaccel (and -hwaccel_output_format, when the frames stay on the GPU) to put before -i;
        //empty for software. d3d11va decodes on the GPU without GPU scaling, so it gets -hwaccel alone
        public List<string> HwAccelArgs
        {
            get
            {
                if (ReferenceEquals(this, Software))
                    return new List<string>();

                var args = new List<string> { "-hwaccel", Candidate };
                if (HwaccelOutputFormat != null)
                {
                    args.Add("-hwaccel_output_format");
                    args.Add(HwaccelOutputFormat);
                }
                return args;
            }
        }

        /// <summary>The -vf graph for this plan: retime, conform to fps, scale, then RGBA with the AVTB time base.</summary>
        /// <remarks>
        /// setpts=PTS/speed plays the source that many times faster, and the fps
        /// filter after it keeps one output frame per timeline frame. A GPU plan
        /// downloads as nv12 before converting to rgba, because hwdownload can
        /// only produce the surface's own format. nv12 assumes 8-bit 4:2:0
        /// content; a 10-bit source's frames are p010le and would need their own
        /// branch.
        /// </remarks>
        public string BuildFilterGraph(Rational fps, int width, int height, Rational? speed = null)
        {
            string retime = speed is not { } s || s == Rational.One ? "" : $"setpts=PTS*{s.Den}/{s.Num},";

            string scale = UsesGpuScale
                ? $"{ScaleFilterName}={width}:{height}"
                : $"scale={width}:{height}";

            string download = UsesGpuScale ? ",hwdownload,format=nv12" : "";

            return $"{retime}fps={FfmpegArgs.Rate(fps)},{scale}{download},format=rgba,settb=AVTB";
        }
    }
}
