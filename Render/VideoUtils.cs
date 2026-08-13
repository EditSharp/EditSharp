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
