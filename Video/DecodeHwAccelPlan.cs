using System.Collections.Generic;

namespace EditSharp.Video
{
    /// <summary>
    /// Resolved decode strategy for one source: which -hwaccel (if any) to
    /// request, whether the scale step runs on the GPU or falls back to CPU,
    /// and the one shared method (BuildFilterGraph) both FfmpegRunner's probe
    /// and SourceDecoder's real decode use to build the actual ffmpeg -vf
    /// string — deliberately ONE method, not two similar-looking ones, so the
    /// probe can never silently drift out of sync with what real decode
    /// actually runs (exactly the gap that let the NVENC/QSV quality-arg bug
    /// through on the encode side: a probe that doesn't build the same string
    /// as the real path can pass while the real path still breaks).
    ///
    /// UsesGpuScale drives two things together: whether -hwaccel_output_format
    /// is included (keeps the decoded frame as a GPU surface instead of an
    /// immediate host download) and whether the scale filter is the GPU one
    /// (e.g. scale_cuda) or the software `scale` filter. A frame that's GPU-
    /// scaled still needs `hwdownload` before it can reach the rawvideo pipe —
    /// pipes are host-memory byte streams, there's no way around that step
    /// with this process-per-source-plus-pipe architecture (true zero-copy
    /// would need ffmpeg linked in-process with real device-handle interop,
    /// which is the same FFmpeg.AutoGen version-gap blocker flagged elsewhere
    /// in the migration manifest) — but hwdownload only pays for moving the
    /// ALREADY-SMALL scaled frame, not the full native-resolution one, which
    /// is the actual saving versus the old CPU-scale-at-native-resolution
    /// path.
    /// </summary>
    internal sealed class DecodeHwAccelPlan
    {
        /// <summary>No hwaccel, no GPU scale — the always-available fallback.</summary>
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

        /// <summary>
        /// The -hwaccel/-hwaccel_output_format args to insert before -i, or an
        /// empty list for software decode (Candidate == "software", or any
        /// candidate with no HwaccelOutputFormat and no ScaleFilterName — i.e.
        /// d3d11va's decode-only case still gets -hwaccel even without GPU
        /// scale, so this checks HwaccelOutputFormat's presence for the second
        /// arg specifically rather than gating the whole list on UsesGpuScale).
        /// </summary>
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

        /// <summary>
        /// The full -vf filter graph string for this plan at the given fps/
        /// target size. GPU-scale plans scale on the GPU then hwdownload the
        /// (already small) result; CPU/software plans scale directly. Both
        /// paths converge on the same `format=rgba,settb=AVTB` tail the
        /// original CPU-only pipeline always used, so SourceDecoder's pipe-
        /// reading contract (rgba8888, AVTB timebase) is unchanged regardless
        /// of which plan produced the frame.
        ///
        /// GPU-SCALE PATH, TWO format FILTERS BACK TO BACK, NOT ONE — this is
        /// load-bearing, not redundant. `hwdownload` negotiates its OWN output
        /// format with whatever filter comes immediately after it; asking for
        /// `format=rgba` directly there fails at runtime with "Invalid output
        /// format rgba for hwframe download" (confirmed against a real
        /// render, not theoretical) because a hardware download can only
        /// produce the surface's native software-equivalent format, not an
        /// arbitrary target. `format=nv12` first satisfies what hwdownload can
        /// actually produce; the SECOND `format=rgba`, now operating on an
        /// already-downloaded software frame, triggers ffmpeg's normal
        /// swscale conversion instead of a hardware-download negotiation.
        ///
        /// HARDCODED nv12 ASSUMES 8-bit 4:2:0 SOURCE CONTENT — nv12 is what
        /// NVDEC/VAAPI/Vulkan hwaccel decode produces for the overwhelmingly
        /// common case (H.264/HEVC 8-bit), but a 10-bit/HDR source's hardware
        /// frame is natively p010le, not nv12, and this would need to detect
        /// that (MediaProbe currently returns dimensions only, not pixel
        /// format/bit depth) and branch accordingly. Flagged, not silently
        /// assumed universal — matches this migration's established practice
        /// of shipping the common case and flagging the edge case rather than
        /// blocking on it.
        /// </summary>
        /// <summary>
        /// `speed` retimes the stream before it is conformed to `fps`:
        /// setpts=PTS/speed makes the source play `speed` times faster, and
        /// the fps filter that follows drops or repeats frames so there is
        /// still exactly one output frame per timeline frame — see
        /// Clip.Speed. setpts only rewrites timestamps, so it sits happily
        /// in front of a GPU-surface chain too.
        /// </summary>
        public string BuildFilterGraph(double fps, int width, int height, double speed = 1d)
        {
            string retime = speed == 1d ? "" : $"setpts=PTS/{FfmpegArgs.Num(speed)},";

            string scale = UsesGpuScale
                ? $"{ScaleFilterName}={width}:{height}"
                : $"scale={width}:{height}";

            string download = UsesGpuScale ? ",hwdownload,format=nv12" : "";

            //round-trip format: a source rate like 30000/1001 must not drift over a long proxy build
            return $"{retime}fps={fps.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},{scale}{download},format=rgba,settb=AVTB";
        }
    }
}
