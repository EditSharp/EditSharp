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
        /// ffmpeg process — decode input, encode output, no filter graph, no
        /// audio. Built for OptimizedMediaBuilder: producing a lossless,
        /// frame-exact-seekable intermediate ahead of a frame-by-frame render,
        /// so per-frame compositor processes never have to decode the ORIGINAL
        /// source (with its own codec, GOP structure, and frame rate)
        /// themselves.
        ///
        /// Unlike AddTrimmedInput's use in MuxAudioVideoAsync, the input seek
        /// here IS frame-accurate. -ss before -i only lands on the nearest
        /// keyframe when the stream is stream-copied — there's nothing to trim
        /// mid-GOP without decoding. This method re-encodes, so ffmpeg decodes
        /// forward from the nearest keyframe to the exact requested timestamp
        /// before the encoder ever sees a frame.
        ///
        /// Only VideoCodec.FFV1 has a pixel format wired up (rgba64le) — see
        /// PixelFormatFor. Other codecs fall back to whatever ffmpeg negotiates
        /// on its own; there's no current caller that needs them.
        /// </summary>
        public static async Task<string> ReencodeVideoAsync(Source source, VideoCodec codec)
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

            args.Add("-c:v");
            args.Add(encoderName);

            string? pixelFormat = PixelFormatFor(codec);
            if (pixelFormat != null)
            {
                args.Add("-pix_fmt");
                args.Add(pixelFormat);
            }

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

            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"ffmpeg exited with code {process.ExitCode}:\n{stderr}");

            return outputPath;
        }

        /// <summary>
        /// The pixel format optimized media is built at for a given codec.
        /// Only FFV1 has one wired up: rgba64le, confirmed to be accepted
        /// cleanly by every filter the frame-by-frame render's per-frame
        /// compositor chain uses (perspective, alphamerge/alphaextract,
        /// colorchannelmixer, fillborders, xfade, overlay, pad), and chosen
        /// over 8-bit rgba to remove any accumulated-rounding concern from
        /// splitting what used to be one filter_complex into many independent
        /// per-frame processes. Returns null for codecs with no forced pixel
        /// format, in which case ffmpeg negotiates one on its own.
        /// </summary>
        private static string? PixelFormatFor(VideoCodec codec) => codec switch
        {
            VideoCodec.FFV1 => "rgba64le",
            _ => null,
        };

        /// <summary>
        /// Container extension for a re-encoded codec's output file. FFV1
        /// needs a real container — matroska is the standard pairing and
        /// supports frame-exact seeking on an intra-only codec like FFV1 with
        /// no GOP-distance cost, which is the entire point of building this
        /// intermediate in the first place.
        /// </summary>
        private static string ContainerExtensionFor(VideoCodec codec) => codec switch
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

            args.Add("-i");
            args.Add(source.Path);
        }
    }
}
