using EditSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using EditSharp.Components;
 
namespace EditSharp.Composite
{
    /// <summary>
    /// Encoder selection for the frame-by-frame render's final mux/encode step
    /// (see Renderer.FinalizeOutputAsync) — resolving a VideoCodec +
    /// HardwareAccelerator into an actual ffmpeg encoder name, including
    /// hardware-encoder probing with a software fallback.
    /// </summary>
    internal static class FfmpegRunner
    {
        /// <summary>
        /// Resolves the actual ffmpeg encoder name and its quality-control flags for
        /// a given VideoCodec + HardwareAccelerator combination. Hardware encoders
        /// need their own encoder names (h264_nvenc/h264_amf/h264_qsv, etc, see
        /// Constants.HardwareCodecNames) AND their own quality-control flag names —
        /// none of them honor -crf the way libx264/libx265 do, and critically they
        /// don't even agree WITH EACH OTHER: NVENC's -preset takes p1-p7 tokens,
        /// QSV's -preset takes named speed tiers, and passing NVENC's "p4" to
        /// hevc_qsv fails at the real encode with exit code -22 ("Undefined
        /// constant... Unable to parse 'preset' option value 'p4'"). See
        /// GetHardwareQualityArgs for the per-vendor split.
        ///
        /// HardwareAccelerator.GPU being requested doesn't guarantee any candidate
        /// is actually usable — needs a matching GPU with current drivers, and older
        /// GPUs don't support every codec (e.g. av1_nvenc needs an RTX 40-series or
        /// newer). Each candidate in priority order gets a trivial 1-frame trial
        /// encode WITH ITS REAL QUALITY ARGS; the first that works wins. If EVERY
        /// hardware candidate fails, this falls back to the software encoder and
        /// reports each candidate's actual ffmpeg error — GPU was requested and the
        /// render is about to run noticeably slower than expected, which should
        /// never be a silent surprise, and "unavailable" with no reason attached is
        /// nearly as unhelpful as silence.
        /// </summary>
        public static async Task<(string EncoderName, List<string> QualityArgs)> GetVideoEncoderSettingsAsync(
            VideoCodec codec, HardwareAccelerator hwAccel)
        {
            if (codec == VideoCodec.GIF)
                return ("gif", new List<string>());
 
            if (hwAccel == HardwareAccelerator.GPU)
            {
                if (Constants.HardwareCodecNames.TryGetValue(codec, out string[]? candidates))
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
 
                    // GPU requested, but no hardware candidate for this codec
                    // worked. Report every candidate's REAL failure, not just
                    // that it "didn't work" — a wrong quality flag, a missing
                    // GPU, an ffmpeg built without that encoder, and a driver
                    // too old for this GPU generation all look identical from
                    // the outside and need completely different fixes.
                    EditSharpConfig.Logger.LogWarning(
                        $"HardwareAccelerator.GPU requested, but no hardware encoder for {codec} is " +
                        "usable on this machine. Falling back to software encoding. Candidates tried:" +
                        Environment.NewLine + string.Join(Environment.NewLine, failures));
                }
                else
                {
                    EditSharpConfig.Logger.LogWarning(
                        $"HardwareAccelerator.GPU requested, but {codec} has no hardware encoder " +
                        "defined at all (see Constants.HardwareCodecNames). Encoding on the CPU.");
                }
            }
 
            string softwareEncoderName = Constants.VideoCodecNames[codec];
            var softwareQualityArgs = new List<string> { "-crf", "21" };
            if (codec == VideoCodec.AV1)
            {
                // libaom-av1 needs -b:v 0 for true CRF mode.
                softwareQualityArgs.Add("-b:v");
                softwareQualityArgs.Add("0");
            }
 
            return (softwareEncoderName, softwareQualityArgs);
        }
 
        /// <summary>
        /// Per-vendor quality-control args, keyed on the winning encoder name's
        /// own suffix (_nvenc/_qsv/_amf) rather than trying to share one flag set
        /// across all three — see GetVideoEncoderSettingsAsync's own remarks for
        /// the real failure this replaces (NVENC's -preset p4 rejected outright by
        /// hevc_qsv).
        ///
        /// QSV and AMF's args below are NOT verified against real hardware — same
        /// honesty flag as Constants.HardwareCodecNames and GpuContext's D3D12
        /// path. Both use each vendor's own "single quality number" mode (QSV:
        /// ICQ via -global_quality, AMF: constant-QP via -rc cqp) chosen as the
        /// closest analogue to NVENC's -cq and libx264's -crf, but the exact flag
        /// names/values come from ffmpeg's encoder documentation rather than a
        /// confirmed encode. Since the probe now runs WITH these args, a wrong
        /// flag makes that candidate fail probing and fall through to the next
        /// one — with the real ffmpeg error logged — instead of sailing through
        /// and breaking the actual render.
        /// </summary>
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
                // ICQ (Intelligent Constant Quality) mode — UNVERIFIED, see class remarks.
                return new List<string> { "-preset", "medium", "-global_quality", "21" };
            }
 
            if (encoderName.EndsWith("_amf", StringComparison.Ordinal))
            {
                // Constant-QP mode — UNVERIFIED, see class remarks.
                return new List<string>
                {
                    "-quality", "balanced",
                    "-rc", "cqp",
                    "-qp_i", "21",
                    "-qp_p", "21",
                    "-qp_b", "21",
                };
            }
 
            // An encoder name that doesn't match any known vendor suffix — rather
            // than guess a THIRD time, use the encoder's own defaults and say so.
            EditSharpConfig.Logger.LogWarning(
                $"No known quality-arg convention for hardware encoder '{encoderName}' — " +
                "using the encoder's own defaults instead of guessing.");
            return new List<string>();
        }
 
        /// <summary>
        /// Resolves the full decode plan (hwaccel args + which scale filter to
        /// use, see DecodeHwAccelPlan) for a source, or DecodeHwAccelPlan.Software
        /// for software decode/scale. Probed against the ACTUAL source, not just
        /// the codec name — hwaccel support depends on the source's own codec/
        /// profile in a way a generic probe can't predict.
        /// HardwareAccelerator.None returns DecodeHwAccelPlan.Software
        /// unconditionally, no probing at all — matches encode's same "None
        /// means an absolute guarantee, not a preference" contract.
        ///
        /// The probe runs each candidate's REAL intended filter chain (hwaccel +
        /// hwaccel_output_format + the GPU scale filter itself + hwdownload),
        /// against a small placeholder size (320x240) rather than the clip's
        /// real decode target — the probe's job is confirming the MECHANISM
        /// works (is scale_cuda actually compiled into this ffmpeg build, etc),
        /// not validating a specific size.
        ///
        /// NOTE this is a MECHANISM probe only (does the filter chain run at
        /// all, exit code 0), not a pixel-correctness probe — see Constants.cs's
        /// own remarks on why the "vulkan" candidate was removed entirely rather
        /// than trusted to this probe: it passed this exact check while still
        /// producing corrupted frames on at least one real machine.
        /// </summary>
        public static async Task<DecodeHwAccelPlan> GetDecodePlanAsync(
            string sourcePath, HardwareAccelerator hwAccel)
        {
            if (hwAccel != HardwareAccelerator.GPU)
                return DecodeHwAccelPlan.Software;
 
            foreach ((string candidate, string? outputFormat, string? scaleFilter) in Constants.DecodeHwAccelCandidates)
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
 
        // Keyed on (candidate, sourcePath) rather than plan identity — hwaccel
        // support can legitimately differ between two sources with different
        // codecs/profiles, unlike encoder availability which only depends on the
        // machine. Cached so re-visiting the same source (e.g. a clip trimmed
        // into two pieces on the timeline) doesn't re-probe.
        private static readonly ConcurrentDictionary<(string Candidate, string Path), Task<bool>>
            DecodePlanAvailabilityCache = new();
 
        private static Task<bool> IsDecodePlanAvailableAsync(DecodeHwAccelPlan plan, string sourcePath) =>
            DecodePlanAvailabilityCache.GetOrAdd(
                (plan.Candidate, sourcePath), _ => ProbeDecodePlanAsync(plan, sourcePath));
 
        /// <summary>
        /// Whether ffmpeg can actually run `plan`'s REAL intended filter chain
        /// against `sourcePath` on this machine right now — a real 1-frame trial
        /// decode at a small placeholder size. On failure, logs ffmpeg's actual
        /// stderr: "unavailable" collapses several genuinely different causes
        /// (ffmpeg built without this hwaccel; a device-selection conflict; this
        /// source's codec/profile not being decodable via this path) into one
        /// boolean that all look identical from the outside without it.
        /// </summary>
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
 
        /// <summary>The outcome of one encoder probe, including WHY it failed.</summary>
        private readonly record struct EncoderProbeResult(bool Succeeded, int ExitCode, string Error)
        {
            public string Describe() =>
                Succeeded
                    ? "ok"
                    : string.IsNullOrWhiteSpace(Error)
                        ? $"exit {ExitCode}, no error output"
                        : $"exit {ExitCode}: {Error.Trim()}";
        }
 
        // Probing spins up a real ffmpeg process, so results are cached per encoder
        // name for the process's lifetime rather than re-probed on every render.
        private static readonly ConcurrentDictionary<string, Task<EncoderProbeResult>> EncoderProbeCache = new();
 
        private static Task<EncoderProbeResult> ProbeEncoderCachedAsync(
            string encoderName, List<string> qualityArgs) =>
            EncoderProbeCache.GetOrAdd(encoderName, _ => ProbeEncoderAsync(encoderName, qualityArgs));
 
        /// <summary>
        /// Whether ffmpeg can actually use the given encoder on this machine right
        /// now — a 1-frame trial encode against a trivial lavfi source, discarded
        /// to null. This is the only reliable way to know: the encoder being
        /// compiled into ffmpeg (which "ffmpeg -encoders" would show) doesn't mean
        /// the hardware and drivers it needs are actually present and current.
        ///
        /// THE PROBE MIRRORS THE REAL ENCODE, deliberately:
        ///   * it passes the SAME quality args the real encode will use. A probe
        ///     that skips them can pass while the real encode dies on a bad flag —
        ///     exactly how NVENC's "-preset p4" reached hevc_qsv and failed there
        ///     with exit -22. Anything wrong with a vendor's flags now costs that
        ///     candidate the probe (and gets logged) instead of the render.
        ///   * it pins -pix_fmt yuv420p, like the real encode does. Hardware
        ///     encoders accept a narrow set of pixel formats, and leaving the
        ///     lavfi source to negotiate one freely makes the probe test a
        ///     different pipeline shape than the render actually uses.
        ///   * it encodes at 1280x720. Hardware encoders reject frame sizes below
        ///     a minimum that varies by GPU and codec generation — 64x64 was small
        ///     enough to fail on genuinely working NVENC hardware, making this
        ///     probe report "unavailable" incorrectly.
        ///
        /// ffmpeg's stderr is captured and returned rather than discarded. A
        /// hardware encoder can fail for reasons that need completely different
        /// fixes — no such GPU, an ffmpeg build without that encoder, a driver
        /// too old for this GPU generation, all NVENC sessions already in use, a
        /// rejected flag — and every one of them collapses to "unavailable"
        /// without the message.
        /// </summary>
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
                // ffmpeg itself failing to start, or any other unexpected error
                // probing — treat as "not available", but keep the reason.
                return new EncoderProbeResult(false, -1, $"probe threw: {ex.Message}");
            }
        }
 
        /// <summary>
        /// Runs ffmpeg with `args` to completion, returning its exit code and
        /// captured stderr. Both redirected streams are drained concurrently with
        /// waiting for exit — otherwise a full output buffer can deadlock the
        /// child process.
        /// </summary>
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
 