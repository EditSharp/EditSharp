using EditSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using EditSharp.Components;

namespace EditSharp.Video
{
    //picks the video encoder for a render and the decode plan for a source, probing hardware and falling back to software
    internal static class FfmpegRunner
    {
        //the encoder and its quality flags for a codec. With the GPU allowed, each hardware encoder is
        //trial-encoded with its real flags, in order, and the first that works is used; if none does,
        //the software encoder is used and every candidate's ffmpeg error is logged
        public static async Task<(string EncoderName, List<string> QualityArgs)> GetVideoEncoderSettingsAsync(
            VideoCodec codec, HardwareAccelerator hwAccel)
        {
            if (codec == VideoCodec.GIF)
                return ("gif", new List<string>());

            if (hwAccel == HardwareAccelerator.GPU)
            {
                if (CodecNames.HardwareCodecNames.TryGetValue(codec, out string[]? candidates))
                {
                    var failures = new List<string>();

                    foreach (string candidateName in candidates)
                    {
                        List<string> qualityArgs = GetHardwareQualityArgs(candidateName);
                        EncoderProbeResult probe = await ProbeEncoderCachedAsync(candidateName, qualityArgs);

                        if (probe.Succeeded)
                        {
                            EditSharpConfig.Logger.Log($"Encode: using hardware encoder '{candidateName}'.");
                            return (candidateName, qualityArgs);
                        }

                        failures.Add($"  {candidateName}: {probe.Describe()}");
                        EditSharpConfig.Logger.LogVerbose(
                            $"Encode: hardware candidate '{candidateName}' unavailable ({probe.Describe()}), trying next.");
                    }

                    //GPU allowed but no hardware encoder worked: say why for each, since the causes need different fixes
                    EditSharpConfig.Logger.LogWarning(
                        $"HardwareAccelerator.GPU requested, but no hardware encoder for {codec} is " +
                        "usable on this machine. Falling back to software encoding. Candidates tried:" +
                        Environment.NewLine + string.Join(Environment.NewLine, failures));
                }
                else
                {
                    EditSharpConfig.Logger.LogWarning(
                        $"HardwareAccelerator.GPU requested, but {codec} has no hardware encoder " +
                        "defined at all (see CodecNames.HardwareCodecNames). Encoding on the CPU.");
                }
            }

            string softwareEncoderName = CodecNames.VideoCodecNames[codec];
            var softwareQualityArgs = new List<string> { "-crf", "21" };
            if (codec == VideoCodec.AV1)
            {
                //libaom-av1 needs -b:v 0 for true CRF mode
                softwareQualityArgs.Add("-b:v");
                softwareQualityArgs.Add("0");
            }

            return (softwareEncoderName, softwareQualityArgs);
        }

        //quality flags by vendor suffix, since NVENC, QSV and AMF name them differently (NVENC's -preset p4 fails on QSV).
        //The QSV (ICQ) and AMF (constant QP) flags come from ffmpeg's documentation, untested on that hardware;
        //a wrong flag fails that encoder's probe instead of the render
        private static List<string> GetHardwareQualityArgs(string encoderName)
        {
            if (encoderName.EndsWith("_nvenc", StringComparison.Ordinal))
            {
                return new List<string>
                {
                    "-preset", "p4",   // balanced speed/quality, modern p1(fastest)-p7(slowest) scale
                    "-rc:v", "vbr",
                    "-cq:v", "21",
                    "-b:v", "0",
                };
            }

            if (encoderName.EndsWith("_qsv", StringComparison.Ordinal))
            {
                //ICQ mode, untested on hardware
                return new List<string> { "-preset", "medium", "-global_quality", "21" };
            }

            if (encoderName.EndsWith("_amf", StringComparison.Ordinal))
            {
                //constant-QP mode, untested on hardware
                return new List<string>
                {
                    "-quality", "balanced",
                    "-rc", "cqp",
                    "-qp_i", "21",
                    "-qp_p", "21",
                    "-qp_b", "21",
                };
            }

            //an unknown vendor: use the encoder's own defaults, and say so
            EditSharpConfig.Logger.LogWarning(
                $"No known quality-arg convention for hardware encoder '{encoderName}'; " +
                "using the encoder's own defaults instead of guessing.");
            return new List<string>();
        }

        //the decode plan for a source: each hardware candidate's real filter chain is trial-decoded
        //against this source at 320x240, and the first that runs wins. None means software, unprobed.
        //The probe checks that the chain runs, not that its pixels are right (see CodecNames on vulkan)
        public static async Task<DecodeHwAccelPlan> GetDecodePlanAsync(
            string sourcePath, HardwareAccelerator hwAccel)
        {
            if (hwAccel != HardwareAccelerator.GPU)
                return DecodeHwAccelPlan.Software;

            foreach ((string candidate, string? outputFormat, string? scaleFilter) in CodecNames.DecodeHwAccelCandidates)
            {
                var plan = new DecodeHwAccelPlan(candidate, outputFormat, scaleFilter);

                if (await IsDecodePlanAvailableAsync(plan, sourcePath))
                {
                    EditSharpConfig.Logger.LogVerbose(
                        $"Decode: using '{candidate}'" +
                        (plan.UsesGpuScale ? $" with GPU scale ('{scaleFilter}')" : " (CPU scale fallback)") +
                        $" for '{sourcePath}'.");
                    return plan;
                }

                EditSharpConfig.Logger.LogVerbose(
                    $"Decode: candidate '{candidate}' unavailable for '{sourcePath}', trying next.");
            }

            EditSharpConfig.Logger.LogWarning(
                $"HardwareAccelerator.GPU requested, but no decode hwaccel works for '{sourcePath}' " +
                "on this machine. Falling back to software decode for this source.");

            return DecodeHwAccelPlan.Software;
        }

        //per candidate and source, since hardware decode support depends on the source's codec and profile
        private static readonly ConcurrentDictionary<(string Candidate, string Path), Task<bool>>
            DecodePlanAvailabilityCache = new();

        private static Task<bool> IsDecodePlanAvailableAsync(DecodeHwAccelPlan plan, string sourcePath) =>
            DecodePlanAvailabilityCache.GetOrAdd(
                (plan.Candidate, sourcePath), _ => ProbeDecodePlanAsync(plan, sourcePath));

        //whether the plan's filter chain decodes one frame of the source; logs ffmpeg's error when it doesn't
        private static async Task<bool> ProbeDecodePlanAsync(DecodeHwAccelPlan plan, string sourcePath)
        {
            try
            {
                var args = new List<string> { "-v", "error" };
                args.AddRange(plan.HwAccelArgs);
                args.AddRange(new[]
                {
                    "-i", sourcePath,
                    "-vf", plan.BuildFilterGraph(fps: 1, width: 320, height: 240),
                    "-frames:v", "1", "-f", "null", "-",
                });

                (int exitCode, string stderr) = await RunFfmpegAsync(args);

                if (exitCode != 0)
                {
                    EditSharpConfig.Logger.LogVerbose(
                        $"Decode: probe for '{plan.Candidate}' against '{sourcePath}' failed " +
                        $"(exit {exitCode}): {stderr.Trim()}");
                }

                return exitCode == 0;
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogVerbose(
                    $"Decode: probe for '{plan.Candidate}' against '{sourcePath}' threw: {ex.Message}");
                return false;
            }
        }

        //one encoder probe's outcome, and why it failed
        private readonly record struct EncoderProbeResult(bool Succeeded, int ExitCode, string Error)
        {
            public string Describe() =>
                Succeeded
                    ? "ok"
                    : string.IsNullOrWhiteSpace(Error)
                        ? $"exit {ExitCode}, no error output"
                        : $"exit {ExitCode}: {Error.Trim()}";
        }

        //each probe starts ffmpeg, so results are kept per encoder for the process's life
        private static readonly ConcurrentDictionary<string, Task<EncoderProbeResult>> EncoderProbeCache = new();

        private static Task<EncoderProbeResult> ProbeEncoderCachedAsync(
            string encoderName, List<string> qualityArgs) =>
            EncoderProbeCache.GetOrAdd(encoderName, _ => ProbeEncoderAsync(encoderName, qualityArgs));

        //one 1280x720 frame encoded to null with the real quality flags and yuv420p, as the render
        //would: an encoder being built into ffmpeg doesn't mean its GPU and driver are present, and
        //smaller frames (64x64) failed on working NVENC hardware. Keeps ffmpeg's error
        private static async Task<EncoderProbeResult> ProbeEncoderAsync(
            string encoderName, List<string> qualityArgs)
        {
            try
            {
                var args = new List<string>
                {
                    "-v", "error",
                    "-f", "lavfi", "-i", "color=black:size=1280x720:rate=30",
                    "-frames:v", "1",
                    "-c:v", encoderName,
                };
                args.AddRange(qualityArgs);
                args.AddRange(new[] { "-pix_fmt", "yuv420p", "-f", "null", "-" });

                (int exitCode, string stderr) = await RunFfmpegAsync(args);
                return new EncoderProbeResult(exitCode == 0, exitCode, stderr);
            }
            catch (Exception ex)
            {
                //ffmpeg failing to start counts as unavailable, keeping the reason
                return new EncoderProbeResult(false, -1, $"probe threw: {ex.Message}");
            }
        }

        //runs ffmpeg to completion, draining both streams while waiting so a full pipe can't stall it
        private static async Task<(int ExitCode, string Stderr)> RunFfmpegAsync(List<string> args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = EditSharpConfig.FfmpegPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi };
            process.Start();

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync());

            return (process.ExitCode, stderrTask.Result);
        }
    }
}