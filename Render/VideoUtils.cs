using EditSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using EditSharp.Components;

namespace EditSharp.Render
{
    /// <summary>
    /// Small, generic ffmpeg-backed video building blocks — muxing, re-encoding
    /// with an optional resize — meant to be reusable both by EditSharp's own
    /// pipeline and by a consumer doing an ordinary video task that has nothing
    /// to do with the timeline renderer. Deliberately kept free of pipeline-
    /// specific filter-graph knowledge: baking a clip's effects into a re-encode
    /// (as OptimizedMediaBuilder does) lives in OptimizedMediaEffectsBaker
    /// instead, not here — see that class for why.
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
        public static async Task<Source> MuxAudioVideoAsync(Source video, Source audio, string outputPath)
        {
            if (video.Type != SourceType.Video)
                throw new ArgumentException($"'{video.Path}' is not a Video source.", nameof(video));

            if (audio.Type != SourceType.Audio && audio.Type != SourceType.Video)
                throw new ArgumentException(
                    $"'{audio.Path}' is neither an Audio nor a Video source.", nameof(audio));

            if (!File.Exists(video.Path))
                throw new FileNotFoundException($"Video input not found: {video.Path}", video.Path);

            if (!File.Exists(audio.Path))
                throw new FileNotFoundException($"Audio input not found: {audio.Path}", audio.Path);

            var args = new List<string> { "-y", "-v", "error" };

            AddTrimmedInput(args, video);
            AddTrimmedInput(args, audio);

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

            return new Source
            {
                Type = SourceType.Video,
                Path = outputPath,
            };
        }

        /// <summary>
        /// Re-encodes a source's video stream to a different codec via a single
        /// ffmpeg process — decode input, encode output, an optional resize,
        /// no audio. Built originally for OptimizedMediaBuilder: producing a
        /// lossless, frame-exact-seekable intermediate ahead of a frame-by-frame
        /// render, so per-frame compositor processes never have to decode the
        /// ORIGINAL source (with its own codec, GOP structure, and frame rate)
        /// themselves — but this is a general-purpose helper, not something
        /// specific to that pipeline; any caller re-encoding a video source to a
        /// different codec, with an optional resize, can use it the same way.
        ///
        /// Unlike AddTrimmedInput's use in MuxAudioVideoAsync, the input seek
        /// here IS frame-accurate. -ss before -i only lands on the nearest
        /// keyframe when the stream is stream-copied — there's nothing to trim
        /// mid-GOP without decoding. This method re-encodes, so ffmpeg decodes
        /// forward from the nearest keyframe to the exact requested timestamp
        /// before the encoder ever sees a frame.
        ///
        /// Only VideoCodec.FFV1 has a pixel format wired up
        /// (PixelFormats.Primary) — see PixelFormatFor. Other codecs fall back
        /// to whatever ffmpeg negotiates on its own; there's no current caller
        /// that needs them.
        ///
        /// scaleTo, when given, downscales to that exact size — the caller is
        /// responsible for having already worked out an aspect-correct,
        /// never-upscaling target; this method just applies whatever box it's
        /// handed.
        ///
        /// Baking effects into a re-encode (as OptimizedMediaBuilder does for a
        /// clip's PreTransform effects) is deliberately NOT a feature of this
        /// method — see OptimizedMediaEffectsBaker, which owns that filter
        /// graph instead, so this stays a small building block rather than
        /// growing pipeline-specific knowledge.
        /// </summary>
        public static async Task<string> ReencodeVideoAsync(
            Source source, VideoCodec codec, (int Width, int Height)? scaleTo = null)
        {
            if (source.Type != SourceType.Video)
                throw new ArgumentException($"'{source.Path}' is not a Video source.", nameof(source));

            if (!File.Exists(source.Path))
                throw new FileNotFoundException($"Input not found: {source.Path}", source.Path);

            if (!Constants.VideoCodecNames.TryGetValue(codec, out string? encoderName))
                throw new NotSupportedException($"ReencodeVideoAsync has no encoder mapping for {codec}.");

            string extension = ContainerExtensionFor(codec);
            string outputPath = GraphUtilities.GetVideoTempFilePath($"reencode_{Guid.NewGuid():N}.{extension}");

            var args = new List<string> { "-y", "-v", "error" };

            AddTrimmedInput(args, source);

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
        /// The pixel format optimized media is built at for a given codec.
        /// Only FFV1 has one wired up: PixelFormats.Primary (gbrap16le) —
        /// referencing that constant rather than a separate hardcoded literal
        /// here matters, not just for tidiness: this used to say "rgba64le"
        /// directly, a SECOND definition of the same nominal value that could
        /// silently drift from PixelFormats.Rgba (as it in fact did — FFV1 has
        /// no packed-RGBA mode, so requesting rgba64le here was always being
        /// silently substituted with gbrap16le by ffmpeg itself, while every
        /// downstream read still assumed genuine rgba64le and paid for an
        /// unaccelerated conversion on every frame to get there). One shared
        /// constant is what keeps the encode request and every downstream read
        /// honestly describing the same bytes.
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
        /// internal rather than private: OptimizedMediaEffectsBaker builds its
        /// own encode args for the same codec and needs the identical answer.
        /// </summary>
        internal static string? PixelFormatFor(VideoCodec codec) => codec switch
        {
            VideoCodec.FFV1 => PixelFormats.Primary,
            _ => null,
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
        /// internal rather than private: OptimizedMediaEffectsBaker needs the
        /// identical container choice for the same codec.
        /// </summary>
        internal static string ContainerExtensionFor(VideoCodec codec) => codec switch
        {
            VideoCodec.FFV1 => "mkv",
            VideoCodec.GIF => "gif",
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
        private static void AddTrimmedInput(List<string> args, Source source)
        {
            args.AddRange(TrimArgsFor(source));
            args.Add("-i");
            args.Add(source.Path);
        }

        /// <summary>
        /// The -ss/-t pair for a source's Start/Duration, or empty when neither
        /// is set. Factored out of AddTrimmedInput so callers registering an
        /// input through an InputGraph's own ExtraArgs (rather than appending
        /// straight to an args list) can use the identical trimming rule.
        ///
        /// internal rather than private: OptimizedMediaEffectsBaker needs the
        /// same trimming rule for the InputGraph-based input it registers.
        /// </summary>
        internal static string[] TrimArgsFor(Source source)
        {
            var args = new List<string>();

            if (source.Start.HasValue)
            {
                args.Add("-ss");
                args.Add(GraphUtilities.Sec(source.Start.Value));
            }

            if (source.Duration.HasValue)
            {
                args.Add("-t");
                args.Add(GraphUtilities.Sec(source.Duration.Value));
            }

            return [.. args];
        }
    }
}
