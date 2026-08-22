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
    /// (see FrameRenderer.FinalizeOutputAsync) — resolving a VideoCodec +
    /// HardwareAccelerator into an actual ffmpeg encoder name, including NVENC
    /// probing with a software fallback.
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
        /// constant... Unable to parse 'preset' option value 'p4'") — this is not
        /// hypothetical, it's a real failure this method used to produce before
        /// GetHardwareQualityArgs existed, because the probe below only confirms
        /// the ENCODER exists and can encode a bare frame with no quality args at
        /// all — it never exercises the quality args themselves, so a wrong preset
        /// name for the winning candidate sails through probing and only breaks at
        /// the real encode. See GetHardwareQualityArgs for the per-vendor split.
        ///
        /// HardwareAccelerator.GPU being requested doesn't guarantee any candidate
        /// is actually usable — needs a matching GPU with current drivers, and older
        /// GPUs don't support every codec (e.g. av1_nvenc needs an RTX 40-series or
        /// newer). Each candidate in priority order gets a trivial 1-frame trial
        /// encode; the first that works wins. If EVERY hardware candidate fails,
        /// this falls back to the software encoder and logs via LogWarning — GPU
        /// was requested and the render is about to run noticeably slower than
        /// expected, which should never be a silent surprise to whoever's watching
        /// the render finish.
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
                    foreach (string candidateName in candidates)
                    {
                        if (await IsEncoderAvailableAsync(candidateName))
                        {
                            EditSharpConfig.Logger.LogVerbose($"Encode: using hardware encoder '{candidateName}'.");
 
                            return (candidateName, GetHardwareQualityArgs(candidateName));
                        }
 
                        EditSharpConfig.Logger.LogVerbose(
                            $"Encode: hardware candidate '{candidateName}' unavailable, trying next.");
                    }
                }
 
                // GPU requested, but no hardware candidate for this codec worked
                // (or none are even defined for it) — fall through to software,
                // loudly, so this is never a silent slowdown.
                EditSharpConfig.Logger.LogWarning(
                    $"HardwareAccelerator.GPU requested, but no hardware encoder for {codec} is " +
                    "usable on this machine. Falling back to software encoding.");
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
        /// Per-vendor quality-control args, keyed on the winning encoder name's
        /// own suffix (_nvenc/_qsv/_amf) rather than trying to share one flag set
        /// across all three — see GetVideoEncoderSettingsAsync's own remarks for
        /// the real failure this replaces (NVENC's -preset p4 rejected outright by
        /// hevc_qsv).
        ///
        /// NVENC's args include adaptive quantization (-spatial-aq/-temporal-aq),
        /// ADDED AFTER A REAL REPORT: high-entropy, incompressible content —
        /// procedural film-grain/noise being the concrete case, but this applies
        /// to any genuinely noisy source — showed visible blocking/macroblock
        /// artifacts under NVENC specifically, while the same content encoded
        /// cleanly through QSV and looked fine when composited (the noise itself
        /// is generated correctly; only the final lossy encode of it was
        /// affected). This is a well-documented NVENC characteristic, not a
        /// EditSharp-side rendering bug: NVENC's rate control under-allocates
        /// bits to fine, spatially/temporally incoherent detail at a flat CQ
        /// target unless adaptive quantization is explicitly turned on, which is
        /// exactly what -spatial-aq/-temporal-aq (plus -aq-strength) correct for
        /// by shifting bits toward high-detail regions instead of spreading them
        /// evenly. -cq is also lowered slightly (21 -> 19) specifically for the
        /// same reason: fine random detail needs a bit more headroom than typical
        /// footage to stay clean at NVENC's default allocation. Ordinary
        /// (non-noisy) content is unaffected by this change beyond a small
        /// bitrate/quality improvement — this isn't gated to noise-only content
        /// because NVENC has no reliable way to detect "this clip contains a
        /// generator/noise node" from the encoder's own side, and there's no
        /// downside to leaving AQ on for normal footage.
        ///
        /// QSV and AMF's args below are NOT verified against a real machine with
        /// that hardware — same honesty flag as Constants.HardwareCodecNames and
        /// GpuContext's D3D12 path, and for the same reason: no such hardware or
        /// ffmpeg build to test against here. Both use each vendor's own "single
        /// quality number" mode (QSV: ICQ via -global_quality, AMF: constant-QP
        /// via -rc cqp) chosen to be the closest analogue to NVENC's -cq/libx264's
        /// -crf, but the exact flag names/values are from ffmpeg encoder
        /// documentation general knowledge, not a confirmed real encode the way
        /// NVENC's now effectively are. If either fails the same way p4 did, the
        /// fix is the same shape: find that vendor's real flag names and replace
        /// the guess below — the probe won't catch it, only a real encode attempt
        /// will, exactly as happened here.
        /// </summary>
        private static List<string> GetHardwareQualityArgs(string encoderName)
        {
            if (encoderName.EndsWith("_nvenc", System.StringComparison.Ordinal))
            {
                return new List<string>
                {
                    "-preset", "p4",   // balanced speed/quality, modern p1(fastest)-p7(slowest) scale
                    "-rc:v", "vbr",
                    "-cq:v", "19",     // slightly below the general-purpose 21 — see class remarks on AQ
                    "-b:v", "0",
                    "-spatial-aq", "1",
                    "-temporal-aq", "1",
                    "-aq-strength", "8", // 1(mild)-15(strong); 8 is NVENC's own documented middle ground
                };
            }
 
            if (encoderName.EndsWith("_qsv", System.StringComparison.Ordinal))
            {
                // ICQ (Intelligent Constant Quality) mode — UNVERIFIED, see class remarks.
                return new List<string> { "-preset", "medium", "-global_quality", "21" };
            }
 
            if (encoderName.EndsWith("_amf", System.StringComparison.Ordinal))
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
        /// profile in a way a generic probe can't predict, same reasoning
        /// GetVideoEncoderSettingsAsync's trial-encode already relies on for
        /// encode. HardwareAccelerator.None returns DecodeHwAccelPlan.Software
        /// unconditionally, no probing at all — matches encode's same "None
        /// means an absolute guarantee, not a preference" contract.
        ///
        /// The probe runs each candidate's REAL intended filter chain (hwaccel +
        /// hwaccel_output_format + the GPU scale filter itself + hwdownload),
        /// against a small placeholder size (320x240) rather than the clip's
        /// real decode target — the probe's job is confirming the MECHANISM
        /// works (is scale_cuda actually compiled into this ffmpeg build, etc),
        /// not validating a specific size, and the real per-clip decode target
        /// isn't known until SkClipContentSource.GetOrOpenDecoder computes it
        /// from the clip's own max-scale — deliberately decoupled so this probe
        /// can run once per source at content-prep time, before any per-clip
        /// sizing exists. This is exactly the lesson the encode-side quality-arg
        /// bug taught: a probe that doesn't exercise the real pipeline shape can
        /// pass while the real pipeline still breaks (there, NVENC's -preset p4
        /// silently broke hevc_qsv because the probe never tried quality args at
        /// all; here, testing bare `-hwaccel` without the scale filter would
        /// have the same blind spot for scale_cuda specifically).
        ///
        /// NOTE this is a MECHANISM probe only (does the filter chain run at
        /// all, exit code 0), not a pixel-correctness probe — see Constants.cs's
        /// own remarks on why the "vulkan" candidate was removed entirely rather
        /// than trusted to this probe: it passed this exact check while still
        /// producing corrupted frames on at least one real machine.
        ///
        /// Same loud-fallback-logging contract as encode: if every hardware
        /// candidate fails for this source, this logs via LogWarning once,
        /// naming the source, before returning DecodeHwAccelPlan.Software.
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
        /// decode at a small placeholder size (320x240; see GetDecodePlanAsync's
        /// remarks on why a placeholder is correct here), same shape and same
        /// reasoning as ProbeEncoderAsync (a flag/filter being recognized by
        /// name doesn't mean the hardware/driver/codec combination actually
        /// works end to end).
        ///
        /// On failure, logs ffmpeg's actual stderr via LogVerbose before
        /// returning false — "unavailable" collapses several genuinely
        /// different failure causes into one boolean (ffmpeg built without
        /// this hwaccel/filter at all; a device-selection conflict, e.g. an
        /// iGPU and a dGPU both enumerating as candidates for the same API;
        /// this specific source's codec/profile not being decodable via this
        /// path) that all look identical from the outside without this. Added
        /// specifically because CUDA unexpectedly lost to d3d11va on a machine
        /// with an active dGPU — don't want to guess at why the same way the
        /// NVENC/QSV quality-arg bug was guessed at before it broke a real
        /// encode; this makes the real ffmpeg error visible instead.
        /// </summary>
        private static async Task<bool> ProbeDecodePlanAsync(DecodeHwAccelPlan plan, string sourcePath)
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
 
                var args = new List<string> { "-v", "error" };
                args.AddRange(plan.HwAccelArgs);
                args.AddRange(new[]
                {
                    "-i", sourcePath,
                    "-vf", plan.BuildFilterGraph(fps: 1, width: 320, height: 240),
                    "-frames:v", "1", "-f", "null", "-",
                });
 
                foreach (string arg in args) psi.ArgumentList.Add(arg);
 
                using var process = new Process { StartInfo = psi };
                process.Start();
 
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync());
 
                if (process.ExitCode != 0)
                {
                    EditSharpConfig.Logger.LogVerbose(
                        $"Decode: probe for '{plan.Candidate}' against '{sourcePath}' failed " +
                        $"(exit {process.ExitCode}): {stderrTask.Result.Trim()}");
                }
 
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogVerbose(
                    $"Decode: probe for '{plan.Candidate}' against '{sourcePath}' threw: {ex.Message}");
                return false;
            }
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
 