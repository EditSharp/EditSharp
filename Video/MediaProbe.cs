using EditSharp;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace EditSharp.Video
{
    /// <summary>What one probe of a media file found.</summary>
    /// <param name="HasVideo">Whether the file has a picture stream, a still image included.</param>
    /// <param name="Width">The picture's width in pixels; 0 without a picture.</param>
    /// <param name="Height">The picture's height in pixels; 0 without a picture.</param>
    /// <param name="HasAudio">Whether the file has an audio stream.</param>
    /// <param name="Duration">How long the file plays; null for a still image or when the file doesn't say.</param>
    /// <param name="IsStillImage">Whether the file is a single picture rather than a video.</param>
    /// <param name="FrameRate">The picture's frames per second; null without a picture or when unknown.</param>
    /// <param name="HasAlpha">Whether the picture's pixel format carries transparency.</param>
    public readonly record struct MediaInfo(
        bool HasVideo,
        int Width,
        int Height,
        bool HasAudio,
        Time? Duration,
        bool IsStillImage,
        Rational? FrameRate,
        bool HasAlpha = false);

    //runs ffprobe (EditSharpConfig.FfprobePath) once per file for streams and format together
    internal static class MediaProbe
    {
        private static string[] ProbeArgs(string path) =>
        [
            "-v", "error",
            "-show_streams",
            "-show_format",
            "-of", "json",
            path,
        ];

        public static async Task<MediaInfo> ProbeAsync(string path) =>
            Parse(await RunFfprobeAsync(ProbeArgs(path)));

        private static readonly ConcurrentDictionary<(string Path, long Length, long LastWriteTicks), Lazy<Task<MediaInfo>>> Cache = new();

        //a finished ProbeCachedAsync result for this file, if there is one; never starts a probe
        public static bool TryGetCached(string path, out MediaInfo info)
        {
            info = default!;
            if (string.IsNullOrEmpty(path)) return false;

            var file = new FileInfo(path);
            if (!file.Exists) return false;

            if (!Cache.TryGetValue((file.FullName, file.Length, file.LastWriteTimeUtc.Ticks), out Lazy<Task<MediaInfo>>? probe)
                || !probe.IsValueCreated || !probe.Value.IsCompletedSuccessfully) return false;

            info = probe.Value.Result;
            return true;
        }

        //ProbeAsync, remembered while the file's path, size and write time are unchanged; concurrent
        //callers share one ffprobe, a missing file throws FileNotFoundException, and a failed probe is retried next time
        public static Task<MediaInfo> ProbeCachedAsync(string path)
        {
            var file = new FileInfo(path);

            if (!file.Exists)
                throw new FileNotFoundException($"Media not found: '{path}'", path);

            var key = (file.FullName, file.Length, file.LastWriteTimeUtc.Ticks);
            Lazy<Task<MediaInfo>> probe = Cache.GetOrAdd(key, _ => new Lazy<Task<MediaInfo>>(() => ProbeAsync(path)));

            return probe.Value.ContinueWith(task =>
            {
                if (!task.IsCompletedSuccessfully) Cache.TryRemove(key, out _);
                return task;
            }, TaskScheduler.Default).Unwrap();
        }

        private static MediaInfo Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            bool hasVideo = false;
            bool hasAudio = false;
            bool hasAlpha = false;
            int width = 0;
            int height = 0;
            Rational? frameRate = null;

            if (root.TryGetProperty("streams", out JsonElement streams))
            {
                foreach (JsonElement stream in streams.EnumerateArray())
                {
                    if (!stream.TryGetProperty("codec_type", out JsonElement type)) continue;

                    switch (type.GetString())
                    {
                        //only the first video stream is measured
                        case "video" when !hasVideo:
                            hasVideo = true;
                            if (stream.TryGetProperty("width", out JsonElement w)) width = w.GetInt32();
                            if (stream.TryGetProperty("height", out JsonElement h)) height = h.GetInt32();
                            if (stream.TryGetProperty("pix_fmt", out JsonElement pixelFormat)) hasAlpha = CarriesAlpha(pixelFormat.GetString());
                            //average first: right for variable-rate phone footage
                            frameRate = ParseRate(stream, "avg_frame_rate") ?? ParseRate(stream, "r_frame_rate");
                            break;

                        case "audio":
                            hasAudio = true;
                            break;
                    }
                }
            }

            Time? duration = null;
            bool isStillImage = false;

            //still images come through the image demuxers: image2 for files
            //by extension, <codec>_pipe when ffprobe sniffed the content
            if (root.TryGetProperty("format", out JsonElement imageFormat) &&
                imageFormat.TryGetProperty("format_name", out JsonElement formatName) &&
                formatName.GetString() is { } name)
            {
                isStillImage = name == "image2" || name.EndsWith("_pipe", StringComparison.Ordinal);
            }

            if (root.TryGetProperty("format", out JsonElement format) &&
                format.TryGetProperty("duration", out JsonElement durationProperty) &&
                durationProperty.GetString() is { } durationText &&
                Rational.TryParse(durationText, out Rational seconds))
            {
                duration = Time.FromSeconds(seconds);
            }

            return new MediaInfo(hasVideo, width, height, hasAudio, isStillImage ? null : duration, isStillImage, frameRate, hasAlpha);
        }

        //ffmpeg names formats with an alpha plane yuva*, gbrap*, ya*, or with an "a" among the rgb letters
        private static bool CarriesAlpha(string? pixelFormat)
        {
            if (string.IsNullOrEmpty(pixelFormat)) return false;
            if (pixelFormat.StartsWith("yuva", StringComparison.Ordinal) || pixelFormat.StartsWith("gbrap", StringComparison.Ordinal) || pixelFormat.StartsWith("ya", StringComparison.Ordinal)) return true;

            string letters = new([.. pixelFormat.TakeWhile(char.IsLetter)]);
            return letters is "rgba" or "bgra" or "argb" or "abgr" or "rgba64" or "bgra64" ? true : letters.Length == 4 && letters.Contains('a') && letters.Contains('r') && letters.Contains('g') && letters.Contains('b');
        }

        //ffprobe rates are fractions ("30000/1001"); "0/0" means unknown
        private static Rational? ParseRate(JsonElement stream, string property)
        {
            if (!stream.TryGetProperty(property, out JsonElement value) || value.GetString() is not { } text) return null;

            return Rational.TryParse(text, out Rational rate) && rate.IsPositive ? rate : null;
        }

        private static ProcessStartInfo BuildStartInfo(string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = EditSharpConfig.FfprobePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            return psi;
        }

        private static async Task<string> RunFfprobeAsync(params string[] args)
        {
            using var process = new Process { StartInfo = BuildStartInfo(args) };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"ffprobe exited with code {process.ExitCode}:\n{stderr}");

            return stdout.ToString();
        }
    }
}