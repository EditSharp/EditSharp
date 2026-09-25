using EditSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using EditSharp.Components.Sources;
using EditSharp.Components.Sources.Audio;
using EditSharp.Components.Sources.Video;
using EditSharp.History;

namespace EditSharp.Video
{
    /// <summary>
    /// Small, generic ffmpeg-backed video building blocks — muxing, re-encoding
    /// with an optional resize — meant to be reusable both by EditSharp's own
    /// pipeline and by a consumer doing an ordinary video task that has nothing
    /// to do with the timeline renderer. Deliberately kept free of pipeline-
    /// specific filter-graph knowledge — a caller with clip/effect-specific
    /// needs (e.g. OptimizedMediaCache's own encode, which needs its own
    /// -profile:v handling) builds its own ffmpeg args using the shared
    /// lookups here (PixelFormatFor, ContainerExtensionFor, ProfileArgsFor,
    /// CodecNames.VideoCodecNames) rather than this file growing pipeline-
    /// specific branches of its own.
    /// </summary>
    public static class VideoUtils
    {
        /// <summary>
        /// Muxes a video source and an audio source into one file at outputPath
        /// with a single ffmpeg process — no InputGraph, no filter_complex, no
        /// pass through the timeline pipeline. Both streams are stream-COPIED,
        /// not re-encoded, so this is cheap but only works when the container at
        /// outputPath can legally hold both codecs as-is.
        /// </summary>
        public static async Task<MediaVideoSource> MuxAudioVideoAsync(MediaVideoSource video, MediaAudioSource audio, string outputPath)
        {
            if (!File.Exists(video.Path))
                throw new FileNotFoundException($"Video input not found: {video.Path}", video.Path);

            if (!File.Exists(audio.Path))
                throw new FileNotFoundException($"Audio input not found: {audio.Path}", audio.Path);

            var args = new List<string> { "-y", "-v", "error" };

            AddTrimmedInput(args, video, video.Path);
            AddTrimmedInput(args, audio, audio.Path);

            args.AddRange(new[]
            {
                "-map", "0:v:0",
                "-map", "1:a:0",
                "-c", "copy",
                //without this the muxer runs to the LONGER of the two inputs and
                //pads the shorter stream's tail with nothing playable — matches
                //FfmpegRunner's own -shortest on the main encode path
                "-shortest",
                outputPath,
            });

            var psi = new ProcessStartInfo
            {
                FileName = EditSharpConfig.FfmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"ffmpeg exited with code {process.ExitCode}:\n{stderr}");

            return Transaction.Suppressed(() => new MediaVideoSource { Path = outputPath });
        }

        /// <summary>
        /// Re-encodes a source's video stream to a different codec via a single
        /// ffmpeg process — decode input, encode output, an optional resize,
        /// no audio. A general-purpose helper, not specific to any one part of
        /// the pipeline; any caller re-encoding a video source to a different
        /// codec, with an optional resize, can use it. (OptimizedMediaCache
        /// does NOT use this method directly — it needs a -profile:v argument
        /// this method has no parameter for, so it builds its own equivalent
        /// ffmpeg invocation instead, sharing PixelFormatFor/
        /// ContainerExtensionFor/ProfileArgsFor with this method rather than
        /// duplicating those lookups.)
        ///
        /// Unlike AddTrimmedInput's use in MuxAudioVideoAsync, the input seek
        /// here IS frame-accurate. -ss before -i only lands on the nearest
        /// keyframe when the stream is stream-copied — there's nothing to trim
        /// mid-GOP without decoding. This method re-encodes, so ffmpeg decodes
        /// forward from the nearest keyframe to the exact requested timestamp
        /// before the encoder ever sees a frame.
        ///
        /// Only VideoCodec.FFV1 has a pixel format wired up
        /// (Ffv1PixelFormat). See PixelFormatFor. Other codecs fall back
        /// to whatever ffmpeg negotiates on its own; there's no current caller
        /// that needs them.
        ///
        /// scaleTo, when given, downscales to that exact size — the caller is
        /// responsible for having already worked out an aspect-correct,
        /// never-upscaling target; this method just applies whatever box it's
        /// handed.
        ///
        /// Baking a clip's effects into a re-encode is deliberately NOT a
        /// feature of this method — nothing in the current pipeline needs
        /// that (SkClipVideoChain/ClipEffects apply a clip's effects live,
        /// per frame, against whatever this method or OptimizedMediaCache
        /// hands back), so this stays a small building block rather than
        /// growing pipeline-specific knowledge.
        /// </summary>
        public static async Task<string> ReencodeVideoAsync(
            MediaVideoSource source, VideoCodec codec, (int Width, int Height)? scaleTo = null)
        {
            if (!File.Exists(source.Path))
                throw new FileNotFoundException($"Input not found: {source.Path}", source.Path);

            if (!CodecNames.VideoCodecNames.TryGetValue(codec, out string? encoderName))
                throw new NotSupportedException($"ReencodeVideoAsync has no encoder mapping for {codec}.");

            string extension = ContainerExtensionFor(codec);
            string outputPath = TempPaths.GetVideoTempFilePath($"reencode_{Guid.NewGuid():N}.{extension}");

            var args = new List<string> { "-y", "-v", "error" };

            //see EditSharpConfig.FilterThreads. The -vf scale below is exactly
            //the kind of filter this applies to — ffmpeg 8.0's swscale is
            //multi-threaded, unlike the effectively-serial one this pipeline
            //was originally written against. Added before AddTrimmedInput
            //because these are GLOBAL options and must precede -i.
            args.AddRange(FfmpegArgs.FilterThreadingArgs());

            AddTrimmedInput(args, source, source.Path);

            if (scaleTo is { } size)
            {
                args.Add("-vf");
                args.Add($"scale={size.Width}:{size.Height},setsar=1");
            }

            args.Add("-c:v");
            args.Add(encoderName);

            string? pixelFormat = PixelFormatFor(codec);
            if (pixelFormat != null)
            {
                args.Add("-pix_fmt");
                args.Add(pixelFormat);
            }

            args.AddRange(MuxerTuningArgsFor(codec));

            //video only — this exists to build optimized media for the
            //frame-by-frame compositor step, which never touches audio; the
            //whole timeline's audio is still mixed separately, once, in
            //AudioMixer
            args.Add("-an");
            args.Add(outputPath);

            var psi = new ProcessStartInfo
            {
                FileName = EditSharpConfig.FfmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            //logged the moment ffmpeg is actually about to be spawned — if a
            //caller reports a hang with no logs, this line (or its absence)
            //is what tells you whether it's stuck BEFORE this method even
            //got called or DURING ffmpeg's own run
            EditSharpConfig.Logger.LogVerbose($"ReencodeVideoAsync starting for '{source.Path}'...");

            var spawnSw = Stopwatch.StartNew();
            process.Start();
            long spawnMs = spawnSw.ElapsedMilliseconds;

            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            var runSw = Stopwatch.StartNew();
            await process.WaitForExitAsync();
            long runMs = runSw.ElapsedMilliseconds;

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"ffmpeg exited with code {process.ExitCode}:\n{stderr}");

            EditSharpConfig.Logger.LogVerbose(
                $"ReencodeVideoAsync for '{source.Path}' done: spawn {spawnMs}ms, " +
                $"run {runMs}ms -> {outputPath}");

            return outputPath;
        }

        /// <summary>
        /// The one pixel format this pipeline forces for optimized media at
        /// all — FFV1's, gbrap16le. Kept as ITS OWN named constant here
        /// (rather than a bare literal inline in PixelFormatFor) for exactly
        /// the reason the previous version of this comment already
        /// documented: this used to say "rgba64le" directly, a second
        /// definition of the same nominal value that could silently drift
        /// from whatever the real encode actually produced — FFV1 has no
        /// packed-RGBA mode, so requesting rgba64le was always being
        /// silently substituted with gbrap16le by ffmpeg itself, while every
        /// downstream read still assumed genuine rgba64le and paid for an
        /// unaccelerated conversion on every frame to get there. A single
        /// named constant is what keeps the encode request and every
        /// downstream read honestly describing the same bytes.
        ///
        /// NOTE: there is no separate `PixelFormats` type anywhere in this
        /// assembly — an earlier pass referenced `PixelFormats.Primary` /
        /// `PixelFormats.Rgba` here without ever actually adding that class,
        /// which doesn't compile. This constant is that fix: the one value
        /// PixelFormatFor's FFV1 case actually needs, defined once, in the
        /// one file that uses it.
        /// </summary>
        internal const string Ffv1PixelFormat = "gbrap16le";

        /// <summary>
        /// The pixel format optimized media is built at for a given codec.
        /// Only FFV1 has one wired up: Ffv1PixelFormat (gbrap16le).
        ///
        /// Confirmed accepted cleanly by every filter the frame-by-frame
        /// render's per-frame compositor chain uses (perspective,
        /// alphamerge/alphaextract, colorchannelmixer, fillborders, xfade,
        /// overlay, pad), and chosen over 8-bit to remove any accumulated-
        /// rounding concern from splitting what used to be one filter_complex
        /// into many independent per-frame processes. Returns null for codecs
        /// with no forced pixel format, in which case ffmpeg negotiates one on
        /// its own.
        ///
        /// DNxHR/ProRes ENTRIES ADDED FOR OptimizedMediaCache — yuv422p
        /// (8-bit 4:2:2) for DNxHR HQ, yuv422p10le (10-bit 4:2:2) for ProRes
        /// HQ, matching each format's own conventional "HQ-tier" pixel
        /// format. NOT build-verified against a real ffmpeg binary in this
        /// sandbox (no ffmpeg toolchain here) — same honesty flag this
        /// project already applies elsewhere (see GpuContext, FfmpegRunner's
        /// QSV/AMF quality args) for anything written from documented
        /// convention rather than a confirmed real encode. These are
        /// extremely standard, well-documented values, but the first real
        /// build against them is the actual verification.
        ///
        /// internal rather than private: OptimizedMediaCache builds its own
        /// encode args for its own codec choice and needs the identical
        /// answer, rather than this pipeline having two independent (and
        /// potentially drifting) definitions of what pixel format a given
        /// codec is built at.
        /// </summary>
        internal static string? PixelFormatFor(VideoCodec codec) => codec switch
        {
            VideoCodec.FFV1 => Ffv1PixelFormat,
            VideoCodec.DNxHR => "yuv422p",
            VideoCodec.ProRes => "yuv422p10le",
            _ => null,
        };

        /// <summary>
        /// The `-profile:v` argument(s) a codec needs to land on a specific
        /// quality tier, or an empty array for a codec with no profile
        /// concept (or where ffmpeg's own default is what's wanted). Added
        /// for OptimizedMediaCache: dnxhd's encoder needs an explicit
        /// dnxhr_* profile to target modern DNxHR (as opposed to legacy
        /// fixed-bitrate DNxHD) at all, and prores_ks's numeric profiles
        /// span a large quality/size range (0=proxy through 5=4444xq) with
        /// no useful default to fall back on.
        ///
        /// Both chosen at the "HQ" quality tier — 4:2:2, not 4:4:4 — as the
        /// general-purpose default for a playback/render proxy; NOT exposed
        /// as its own EditSharpConfig knob in this pass (kept to codec
        /// choice + a resolution cap, per the two knobs actually decided in
        /// conversation) — a real need for a different tier is a small,
        /// contained follow-up here, not a redesign.
        /// </summary>
        internal static string[] ProfileArgsFor(VideoCodec codec) => codec switch
        {
            VideoCodec.DNxHR => ["-profile:v", "dnxhr_hq"],
            VideoCodec.ProRes => ["-profile:v", "3"], // prores_ks: 3 = "hq"
            _ => [],
        };

        /// <summary>
        /// Muxer-level tuning for optimized media's seek performance. Only
        /// matters for FFV1's matroska container.
        ///
        /// -cluster_time_limit forces a new cluster roughly every frame
        /// (default is several SECONDS worth of frames per cluster) — matters
        /// because matroska's Cues (seek index) points at CLUSTER
        /// boundaries, not individual frames, so a -ss seek is really two
        /// steps: jump to the nearest cluster via Cues (cheap), then decode
        /// FORWARD from that cluster's start to the exact target frame. With
        /// several seconds per cluster, that forward step means decoding
        /// dozens of frames on every single seek — and since later seek
        /// targets land in later clusters just as far past their own
        /// cluster's start, the cost doesn't even shrink for early frames,
        /// it's paid on every read regardless of position. This was
        /// confirmed directly: the SAME clips' decode_video bench time grew
        /// substantially between a near-zero seek offset (frame 0) and one
        /// ~100 frames in, with everything else identical. 1ms is small
        /// enough to force a cluster boundary at (or within a couple of)
        /// every frame for any real framerate, which reduces that forward
        /// step to effectively zero — FFV1 is intra-only, so every frame is
        /// independently decodable the moment the cluster starts.
        ///
        /// Costs a little container overhead (more, smaller clusters means
        /// more per-cluster header bytes) — negligible next to what it saves
        /// on every one of hundreds of per-frame seeks.
        ///
        /// DNxHR/ProRes need NO equivalent tuning — see OptimizedMediaCache.
        /// EncodeAsync's own remarks: mov/mp4's sample tables are built
        /// per-sample regardless of GOP size, and both codecs are all-intra,
        /// so a -ss seek into either is already frame-accurate with nothing
        /// extra to configure at encode time.
        /// </summary>
        internal static string[] MuxerTuningArgsFor(VideoCodec codec) => codec switch
        {
            VideoCodec.FFV1 => ["-cluster_time_limit", "1"],
            _ => [],
        };

        /// <summary>
        /// Container extension for a re-encoded codec's output file. FFV1
        /// needs a real container — matroska is the standard pairing and
        /// supports frame-exact seeking on an intra-only codec like FFV1 with
        /// no GOP-distance cost, which is the entire point of building this
        /// intermediate in the first place.
        ///
        /// DNxHR and ProRes BOTH use "mov" — the standard container for
        /// either on any platform, and ffmpeg happily muxes dnxhd into mov
        /// (not just the more Avid-specific MXF). Sharing one extension
        /// between the two matters to OptimizedMediaCache specifically: it's
        /// what lets switching EditSharpConfig.OptimizedMediaCodec reuse the
        /// exact same hash-addressed filename for a rebuilt entry rather
        /// than leaving an orphaned file from the previously configured
        /// codec behind — see that class's TryLoadExistingAsync, which
        /// relies on this collision (and disambiguates it via the meta
        /// file's own Codec field) rather than avoiding it.
        ///
        /// internal rather than private: OptimizedMediaCache needs the
        /// identical container choice for the same codec, for the same
        /// single-source-of-truth reason as PixelFormatFor above.
        /// </summary>
        internal static string ContainerExtensionFor(VideoCodec codec) => codec switch
        {
            VideoCodec.FFV1 => "mkv",
            VideoCodec.GIF => "gif",
            VideoCodec.DNxHR => "mov",
            VideoCodec.ProRes => "mov",
            _ => "mp4",
        };

        /// <summary>
        /// The ffmpeg MUXER name (`-f` argument) for a codec's container —
        /// NOT always the same string as its file extension
        /// (ContainerExtensionFor), which is why this exists as its own
        /// lookup rather than callers just passing the extension to `-f`
        /// directly (mkv's muxer is named "matroska", not "mkv").
        ///
        /// ADDED TO FIX A REAL BUG: OptimizedMediaCache used to rely on
        /// ffmpeg SNIFFING the output muxer from its output path's file
        /// extension (the ordinary, usually-fine ffmpeg behavior when no
        /// `-f` is given) — which broke the moment its temp-file naming put
        /// anything after that extension ("...hash.mov.tmp-GUID"), since
        /// ffmpeg had no ".tmp-GUID" muxer to sniff and failed outright
        /// ("Unable to choose an output format", exit -22). Passing `-f`
        /// explicitly means the actual output PATH's shape is irrelevant to
        /// muxer selection — this is now the single source of truth for
        /// "what format is this codec's container", the way
        /// ContainerExtensionFor already is for "what's its file extension".
        ///
        /// internal rather than private: OptimizedMediaCache calls this
        /// directly. ReencodeVideoAsync above does NOT (it still relies on
        /// extension sniffing) since its own output paths are always
        /// freshly generated via TempPaths.GetVideoTempFilePath with no
        /// further suffix ever appended afterward — nothing has reintroduced
        /// that specific bug shape for it. A future caller doing the same
        /// kind of temp-then-rename dance OptimizedMediaCache.BuildAsync
        /// does should use this too, rather than assume extension sniffing
        /// is safe by default.
        /// </summary>
        internal static string ContainerFormatNameFor(VideoCodec codec) => codec switch
        {
            VideoCodec.FFV1 => "matroska",
            VideoCodec.GIF => "gif",
            VideoCodec.DNxHR => "mov",
            VideoCodec.ProRes => "mov",
            _ => "mp4",
        };

        /// <summary>
        /// Adds a source's -i, with -ss/-t placed BEFORE it when Source.Start or
        /// Source.Duration are set, so ffmpeg seeks on the demuxer instead of
        /// decoding from the front and discarding frames afterward. Because the
        /// muxed streams are stream-copied rather than re-encoded, this seek can
        /// only land on a keyframe — accurate-to-the-sample trimming would need
        /// a re-encode, which this function deliberately avoids.
        /// </summary>
        private static void AddTrimmedInput(List<string> args, Source source, string path)
        {
            args.AddRange(TrimArgsFor(source));
            args.Add("-i");
            args.Add(path);
        }

        /// <summary>
        /// The -ss/-t pair for a source's Start/Duration, or empty when neither
        /// is set. Factored out of AddTrimmedInput so callers registering an
        /// input through an InputGraph's own ExtraArgs (rather than appending
        /// straight to an args list) can use the identical trimming rule.
        ///
        /// internal rather than private: other pipeline-internal ffmpeg-arg
        /// builders elsewhere in this assembly need the same trimming rule
        /// for an InputGraph-based input they register.
        /// </summary>
        internal static string[] TrimArgsFor(Source source)
        {
            var args = new List<string>();

            if (source.Start.HasValue)
            {
                args.Add("-ss");
                args.Add(FfmpegArgs.Sec(source.Start.Value));
            }

            if (source.Duration.HasValue)
            {
                args.Add("-t");
                args.Add(FfmpegArgs.Sec(source.Duration.Value));
            }

            return [.. args];
        }
    }
}
