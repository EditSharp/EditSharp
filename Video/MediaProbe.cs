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
    //what one ffprobe call says about a file; Duration is null for still images
    internal readonly record struct MediaInfo(
        bool HasVideo,
        int Width,
        int Height,
        bool HasAudio,
        TimeSpan? Duration,
        bool IsStillImage,
        double? FrameRate);

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
            int width = 0;
            int height = 0;
            double? frameRate = null;

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
                            //average first: right for variable-rate phone footage
                            frameRate = ParseRate(stream, "avg_frame_rate") ?? ParseRate(stream, "r_frame_rate");
                            break;

                        case "audio":
                            hasAudio = true;
                            break;
                    }
                }
            }

            TimeSpan? duration = null;
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
                double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double seconds))
            {
                duration = TimeSpan.FromSeconds(seconds);
            }

            return new MediaInfo(hasVideo, width, height, hasAudio, isStillImage ? null : duration, isStillImage, frameRate);
        }

        //ffprobe rates are fractions ("30000/1001"); "0/0" means unknown
        private static double? ParseRate(JsonElement stream, string property)
        {
            if (!stream.TryGetProperty(property, out JsonElement value) || value.GetString() is not { } text) return null;

            string[] parts = text.Split('/');
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator) ||
                numerator <= 0 || denominator <= 0)
                return null;

            return numerator / denominator;
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