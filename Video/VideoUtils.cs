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
    /// <summary>Everyday ffmpeg tasks outside the timeline renderer: joining streams and re-encoding.</summary>
    public static class VideoUtils
    {
        /// <summary>Joins a video and an audio source into one file without re-encoding either.</summary>
        /// <remarks>Both streams are copied, so it's fast, but the output container must accept both codecs as they are. Each source's Start and Duration trim it, landing on the nearest keyframe. The output ends with the shorter stream.</remarks>
        /// <param name="video">The video source; its video stream is used.</param>
        /// <param name="audio">The audio source; its audio stream is used.</param>
        /// <param name="outputPath">The file to write; its extension picks the container.</param>
        /// <returns>A source for the new file.</returns>
        /// <exception cref="FileNotFoundException">Either input doesn't exist.</exception>
        /// <exception cref="InvalidOperationException">ffmpeg failed.</exception>
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
                //otherwise the output runs as long as the longer input
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

        /// <summary>Re-encodes a source's video to another codec, optionally resized, with no audio.</summary>
        /// <remarks>The source's Start and Duration trim it frame-accurately, since ffmpeg decodes from the nearest keyframe to the exact time. The file goes under <see cref="EditSharpConfig.TempDirectory"/>.</remarks>
        /// <param name="source">The source to re-encode.</param>
        /// <param name="codec">The codec to encode with.</param>
        /// <param name="scaleTo">The exact output size, already aspect-correct; null keeps the source's size.</param>
        /// <returns>The path of the new file.</returns>
        /// <exception cref="FileNotFoundException">The source file doesn't exist.</exception>
        /// <exception cref="NotSupportedException"><paramref name="codec"/> has no encoder.</exception>
        /// <exception cref="InvalidOperationException">ffmpeg failed.</exception>
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

            //global options, so before -i; see EditSharpConfig.FilterThreads
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

        //FFV1 has no packed RGBA mode; asking for rgba64le gets gbrap16le anyway
        internal const string Ffv1PixelFormat = "gbrap16le";

        //the pixel format a codec is encoded at; null lets ffmpeg choose. DNxHR and ProRes use their HQ-tier 4:2:2 formats
        internal static string? PixelFormatFor(VideoCodec codec) => codec switch
        {
            VideoCodec.FFV1 => Ffv1PixelFormat,
            VideoCodec.DNxHR => "yuv422p",
            VideoCodec.ProRes => "yuv422p10le",
            _ => null,
        };

        //the profile a codec needs for its HQ tier: dnxhd needs a dnxhr_* profile to write DNxHR at all,
        //and prores_ks has no useful default among its six
        internal static string[] ProfileArgsFor(VideoCodec codec) => codec switch
        {
            VideoCodec.DNxHR => ["-profile:v", "dnxhr_hq"],
            VideoCodec.ProRes => ["-profile:v", "3"], // prores_ks: 3 = "hq"
            _ => [],
        };

        //matroska's seek index points at clusters, and a seek decodes forward from one;
        //a new cluster about every frame makes seeks into FFV1 land straight on the frame
        internal static string[] MuxerTuningArgsFor(VideoCodec codec) => codec switch
        {
            VideoCodec.FFV1 => ["-cluster_time_limit", "1"],
            _ => [],
        };

        internal static string ContainerExtensionFor(VideoCodec codec) => codec switch
        {
            VideoCodec.FFV1 => "mkv",
            VideoCodec.GIF => "gif",
            VideoCodec.DNxHR => "mov",
            VideoCodec.ProRes => "mov",
            _ => "mp4",
        };

        //-ss and -t before -i, so ffmpeg seeks in the demuxer instead of decoding from the start
        private static void AddTrimmedInput(List<string> args, Source source, string path)
        {
            args.AddRange(TrimArgsFor(source));
            args.Add("-i");
            args.Add(path);
        }

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
