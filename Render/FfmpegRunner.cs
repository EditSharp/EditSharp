using EditSharp;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using EditSharp.Components;

namespace EditSharp.Render
{
    /// <summary>
    /// Encoder selection for the frame-by-frame render's final mux/encode step
    /// (see FrameRenderer.FinalizeOutputAsync) — resolving a VideoCodec +
    /// HardwareAccelerator into an actual ffmpeg encoder name, including NVENC
    /// probing with a software fallback.
    /// </summary>
    internal static class FfmpegRunner
    {
        /// <summary>
        /// Resolves the actual ffmpeg encoder name and its quality-control flags for
        /// a given VideoCodec + HardwareAccelerator combination. NVENC needs its own
        /// encoder names (h264_nvenc, hevc_nvenc, av1_nvenc) and its own quality
        /// mechanism — it doesn't honor -crf the way libx264/libx265 do. Its constant
        /// -quality equivalent is VBR rate control driven by -cq with no bitrate cap
        /// (-b:v 0), analogous to CRF: lower -cq is higher quality.
        ///
        /// HardwareAccelerator.Nvenc being requested doesn't guarantee it's actually
        /// usable — requires an NVIDIA GPU with current drivers, and older GPUs
        /// don't support every codec (e.g. av1_nvenc needs an RTX 40-series or
        /// newer). Rather than letting the whole render fail on a machine without a
        /// compatible GPU, this probes the specific NVENC encoder with a trivial
        /// 1-frame trial encode first and falls back to the software encoder if
        /// that fails.
        /// </summary>
        public static async Task<(string EncoderName, List<string> QualityArgs)> GetVideoEncoderSettingsAsync(
            VideoCodec codec, HardwareAccelerator hwAccel)
        {
            if (codec == VideoCodec.GIF)
                return ("gif", new List<string>());

            if (hwAccel == HardwareAccelerator.Nvenc)
            {
                if (!Constants.NvencCodecNames.TryGetValue(codec, out string? nvencEncoderName))
                    throw new System.NotSupportedException($"No NVENC encoder available for {codec}.");

                if (await IsEncoderAvailableAsync(nvencEncoderName))
                {
                    return (nvencEncoderName, new List<string>
                    {
                        "-preset", "p4",   // balanced speed/quality, modern p1(fastest)-p7(slowest) scale
                        "-rc:v", "vbr",
                        "-cq:v", "21",
                        "-b:v", "0",
                    });
                }
                // Requested but not actually usable on this machine — fall through
                // to the software encoder below instead of failing the render.
            }

            string softwareEncoderName = Constants.VideoCodecNames[codec];
            var qualityArgs = new List<string> { "-crf", "21" };
            if (codec == VideoCodec.AV1)
            {
                // libaom-av1 needs -b:v 0 for true CRF mode.
                qualityArgs.Add("-b:v");
                qualityArgs.Add("0");
            }

            return (softwareEncoderName, qualityArgs);
        }

        // Probing spins up a real ffmpeg process, so results are cached per encoder
        // name for the process's lifetime rather than re-probed on every render.
        private static readonly ConcurrentDictionary<string, Task<bool>> EncoderAvailabilityCache = new();

        private static Task<bool> IsEncoderAvailableAsync(string encoderName) =>
            EncoderAvailabilityCache.GetOrAdd(encoderName, ProbeEncoderAsync);

        /// <summary>
        /// Whether ffmpeg can actually use the given encoder on this machine right
        /// now — a 1-frame trial encode against a trivial lavfi source, discarded to
        /// null. This is the only reliable way to know: the encoder being compiled
        /// into ffmpeg (which "ffmpeg -encoders" would show) doesn't mean the
        /// hardware/drivers it needs are actually present.
        /// </summary>
        private static async Task<bool> ProbeEncoderAsync(string encoderName)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = EditSharpConfig.FfmpegPath,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var arg in new[]
                {
                    // NVENC (and hardware encoders generally) reject frame sizes
                    // below a minimum that varies by GPU/codec generation — 64x64
                    // was small enough to fail even on a genuinely working NVENC
                    // setup, making this probe report "unavailable" incorrectly.
                    // 1280x720 stays safely above any known NVENC minimum while
                    // still being a trivial, near-instant 1-frame encode.
                    "-v", "error", "-f", "lavfi", "-i", "color=black:size=1280x720:rate=30",
                    "-frames:v", "1", "-c:v", encoderName, "-f", "null", "-",
                })
                {
                    psi.ArgumentList.Add(arg);
                }

                using var process = new Process { StartInfo = psi };
                process.Start();

                // Drain both redirected streams concurrently with waiting for exit
                // — otherwise a full output buffer can deadlock the child process.
                Task stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task stderrTask = process.StandardError.ReadToEndAsync();
                await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync());

                return process.ExitCode == 0;
            }
            catch
            {
                // ffmpeg itself failing to start, or any other unexpected error
                // probing — treat as "not available" rather than propagating.
                return false;
            }
        }
    }
}
