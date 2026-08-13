using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using static EditSharp.Render.GraphUtilities;
using EditSharp.Assembly;

namespace EditSharp.Render
{
    /// <summary>
    /// Step 4 of the assembly pipeline: run ffmpeg directly via Process (no
    /// FFMpegCore dependency), using the Blueprint's own resolution/framerate/
    /// codec choices, plus encoder selection (including NVENC probing/fallback).
    /// </summary>
    internal static class FfmpegRunner
    {
        public static async Task RunFfmpegAsync(
            InputGraph graph, string finalVideoLabel, string finalAudioLabel, Blueprint blueprint, int fps)
        {
            if (graph.Inputs.Count == 0)
                throw new System.InvalidOperationException("No inputs were registered.");

            foreach (var input in graph.Inputs)
            {
                if (input.VerifyExists && !File.Exists(input.Path))
                    throw new FileNotFoundException($"Input file not found: {input.Path}", input.Path);
            }

            // The filter_complex string can get very large (many clips/overlays/
            // transitions), enough to exceed the OS's command-line length limit when
            // passed inline. The dedicated -filter_complex_script option that used
            // to solve this was deprecated (and is gone in this build) in favor of a
            // more general mechanism: prefixing ANY option name with '/' tells
            // ffmpeg to read that option's value from a file instead of taking it
            // inline. So the current correct form is "-/filter_complex <path>", not
            // a separately-named flag — confirmed both by ffmpeg's own docs
            // (ffmpeg-filters.html) and by the ffmpeg-devel patch that deprecated
            // -filter_complex_script, whose commit message states it's "equivalent
            // to -/filter_complex".
            string filterComplex = string.Join(";", graph.FilterLines);
            string scriptPath = GetVideoTempFilePath($"filter_{System.Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(scriptPath, filterComplex);

            bool isGif = blueprint.VideoCodec == VideoCodec.GIF;
            var (videoEncoderName, videoQualityArgs) =
                await GetVideoEncoderSettingsAsync(blueprint.VideoCodec, blueprint.HardwareAccelerator);

            // Built as a real token list (one array entry per argument) and passed
            // via ProcessStartInfo.ArgumentList below — each token reaches ffmpeg
            // exactly as written, with no intermediate string-splitting/escaping
            // logic to second-guess.
            // -v error keeps stderr to the actual failure. Raise to "debug" when a
            // graph refuses to bind: it prints each filter's negotiated formats and
            // dimensions as the graph configures, which the higher-level error
            // summaries in this build have repeatedly got wrong.
            var args = new List<string> { "-y", "-v", "error" };

            // Resolved ONCE for the whole run rather than per input: the probe
            // spawns a process, and every input on a given render wants the same
            // answer anyway. Comes back empty when hardware decode is off or when
            // the requested device isn't really there.
            string[] decodeArgs = await GetHardwareDecodeArgsAsync(blueprint.HardwareDecoder);

            foreach (var input in graph.Inputs)
            {
                // -hwaccel is a PER-INPUT option and has to precede that input's
                // -i, which is exactly where ExtraArgs already goes. Only real
                // video files get it — see InputGraph.AddInput for why applying it
                // to the pipeline's PNGs would fail the run rather than be ignored.
                if (input.HardwareDecodable) args.AddRange(decodeArgs);
                if (input.ExtraArgs != null) args.AddRange(input.ExtraArgs);
                args.Add("-i");
                args.Add(input.Path);
            }

            args.Add("-/filter_complex");
            args.Add(scriptPath);

            args.Add("-map");
            args.Add($"[{finalVideoLabel}]");
            if (!isGif)
            {
                args.Add("-map");
                args.Add($"[{finalAudioLabel}]");
            }

            if (isGif)
            {
                args.Add("-c:v");
                args.Add("gif");
            }
            else
            {
                args.Add("-c:v");
                args.Add(videoEncoderName);
                args.AddRange(videoQualityArgs);

                string audioCodecName = Constants.AudioCodecNames[blueprint.AudioCodec];
                args.Add("-c:a");
                args.Add(audioCodecName);
                args.Add("-b:a");
                args.Add("192k");
                args.Add("-shortest");
                args.Add("-pix_fmt");
                args.Add("yuv420p");
            }

            args.Add("-r");
            args.Add(fps.ToString(CultureInfo.InvariantCulture));
            args.Add(blueprint.OutputDirectory);

            var psi = new ProcessStartInfo
            {
                FileName = EditSharpConfig.FfmpegPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var stderr = new System.Text.StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                throw new System.InvalidOperationException(
                    $"ffmpeg exited with code {process.ExitCode}:\n{stderr}\n\n" +
                    $"Filter script preserved for inspection at: {scriptPath}");

            // Only clean up on success — a failed run's script stays on disk so the
            // actual generated filter graph can be inspected directly, rather than
            // inferred from ffmpeg's (not always literal) summary output.
            try { File.Delete(scriptPath); } catch { /* best-effort cleanup */ }
        }

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

        /// <summary>
        /// The per-input flags that put DECODE on the GPU, or an empty array for
        /// software decode.
        ///
        /// -hwaccel_output_format is deliberately never set. With it, decoded
        /// frames stay in GPU memory and every filter downstream has to be a
        /// hardware filter or be wrapped in hwdownload/hwupload — and this
        /// pipeline's chain is almost entirely filters with no hardware
        /// equivalent (alphaextract, alphamerge, fillborders, blend, xfade,
        /// perspective). Without it the frames are handed back in system memory
        /// and the filter graph is bit-for-bit the same graph it is today, so this
        /// change can only affect decode time and nothing else.
        ///
        /// The two values behave quite differently on a machine that can't do
        /// what's asked, which is why Cuda is probed and Auto isn't:
        ///   -hwaccel auto  falls back to software on its own — verified on a
        ///                  machine with no GPU: exit 0, output byte-identical to
        ///                  a software decode.
        ///   -hwaccel cuda  does NOT — verified: exit 255, "No device available
        ///                  for decoder: device type cuda needed for codec h264",
        ///                  and the render is lost. So it is probed first and
        ///                  silently downgraded, matching how NVENC is handled.
        /// </summary>
        public static async Task<string[]> GetHardwareDecodeArgsAsync(HardwareDecoder decoder) =>
            decoder switch
            {
                HardwareDecoder.None => [],
                HardwareDecoder.Auto => ["-hwaccel", "auto"],
                HardwareDecoder.Cuda => await IsHardwareDeviceAvailableAsync("cuda")
                    ? ["-hwaccel", "cuda"]
                    : [],
                _ => throw new System.NotSupportedException(
                    $"Unknown HardwareDecoder value: {decoder}."),
            };

        // Probing spins up a real ffmpeg process, so results are cached per encoder
        // name for the process's lifetime rather than re-probed on every render.
        private static readonly ConcurrentDictionary<string, Task<bool>> EncoderAvailabilityCache = new();

        private static readonly ConcurrentDictionary<string, Task<bool>> DeviceAvailabilityCache = new();

        private static Task<bool> IsHardwareDeviceAvailableAsync(string deviceType) =>
            DeviceAvailabilityCache.GetOrAdd(deviceType, ProbeHardwareDeviceAsync);

        /// <summary>
        /// Whether ffmpeg can actually construct the given hardware device on this
        /// machine — the same question the NVENC probe asks about an encoder, and
        /// for the same reason: "cuda" appearing in `ffmpeg -hwaccels` only means
        /// the support was compiled in, not that a driver or a GPU is present.
        ///
        /// -init_hw_device is a GLOBAL option, so a failure to create the device
        /// aborts during option parsing, before anything is decoded — the probe
        /// costs essentially nothing and never touches a real source file. The
        /// trivial lavfi pipeline after it exists only to give ffmpeg a valid
        /// command line to fail (or succeed) at; verified that the same command
        /// WITHOUT -init_hw_device exits 0, so a nonzero exit here is attributable
        /// to device creation and nothing else.
        /// </summary>
        private static async Task<bool> ProbeHardwareDeviceAsync(string deviceType)
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
                    "-v", "error",
                    "-init_hw_device", $"{deviceType}=probe",
                    "-f", "lavfi", "-i", "color=black:size=64x64:rate=1",
                    "-frames:v", "1", "-f", "null", "-",
                })
                {
                    psi.ArgumentList.Add(arg);
                }

                using var process = new Process { StartInfo = psi };
                process.Start();

                // Both streams drained concurrently with the wait, same as the
                // encoder probe — a full pipe buffer can otherwise deadlock the
                // child.
                Task stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task stderrTask = process.StandardError.ReadToEndAsync();
                await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync());

                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

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
