using EditSharp;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace EditSharp.Composite
{
    /// <summary>
    /// Everything the assembler needs to know about a media file, read in ONE
    /// ffprobe call.
    ///
    /// Duration is null when the container reports none, which is normal for
    /// still images.
    /// </summary>
    internal readonly record struct MediaInfo(
        bool HasVideo,
        int Width,
        int Height,
        bool HasAudio,
        TimeSpan? Duration);

    /// <summary>
    /// Thin wrapper around ffprobe (invoked directly via Process, no FFMpegCore
    /// dependency). Resolves "ffprobe" from PATH, same as how FfmpegRunner
    /// resolves "ffmpeg".
    ///
    /// ProbeAsync deliberately asks for everything at once. The assembler needs
    /// three facts about most sources — pixel dimensions, duration, and whether
    /// there is an audio stream at all — and fetching them separately meant
    /// spawning three ffprobe processes per clip, which on a timeline of any size
    /// is most of the time spent before ffmpeg even starts. One call with
    /// -show_streams and -show_format answers all three.
    /// </summary>
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

        /// <summary>
        /// Blocking probe, for authoring code that has no async context to await
        /// in — sizing a clip to its media from a property setter or constructor,
        /// for instance.
        ///
        /// This is a real synchronous implementation rather than .Result on the
        /// async one. Blocking on a Task that resumes on a captured
        /// synchronization context deadlocks on a UI thread, which is precisely
        /// where a synchronous overload is most likely to get called.
        ///
        /// It still launches a process and waits for it, so it is not free — do not
        /// call it in a loop on a thread that has a window to keep repainting.
        /// </summary>
        public static MediaInfo Probe(string path) => Parse(RunFfprobe(ProbeArgs(path)));

        private static MediaInfo Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            bool hasVideo = false;
            bool hasAudio = false;
            int width = 0;
            int height = 0;

            if (root.TryGetProperty("streams", out JsonElement streams))
            {
                foreach (JsonElement stream in streams.EnumerateArray())
                {
                    if (!stream.TryGetProperty("codec_type", out JsonElement type)) continue;

                    switch (type.GetString())
                    {
                        //only the FIRST video stream is measured, matching the
                        //v:0 selection the separate calls used to make
                        case "video" when !hasVideo:
                            hasVideo = true;
                            if (stream.TryGetProperty("width", out JsonElement w)) width = w.GetInt32();
                            if (stream.TryGetProperty("height", out JsonElement h)) height = h.GetInt32();
                            break;

                        case "audio":
                            hasAudio = true;
                            break;
                    }
                }
            }

            TimeSpan? duration = null;
            if (root.TryGetProperty("format", out JsonElement format) &&
                format.TryGetProperty("duration", out JsonElement durationProperty) &&
                durationProperty.GetString() is { } durationText &&
                double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double seconds))
            {
                duration = TimeSpan.FromSeconds(seconds);
            }

            return new MediaInfo(hasVideo, width, height, hasAudio, duration);
        }

        /// <summary>
        /// Native pixel dimensions. Prefer ProbeAsync when more than one fact about
        /// the file is needed — this spawns a process of its own.
        /// </summary>
        public static async Task<(int Width, int Height)> GetDimensionsAsync(string path)
        {
            MediaInfo info = await ProbeAsync(path);

            if (!info.HasVideo)
                throw new InvalidOperationException(
                    $"'{path}' has no video/image stream to read dimensions from.");

            return (info.Width, info.Height);
        }

        /// <summary>
        /// Total duration of the file. Prefer ProbeAsync when more than one fact
        /// about the file is needed — this spawns a process of its own.
        /// </summary>
        public static async Task<TimeSpan> GetDurationAsync(string path)
        {
            MediaInfo info = await ProbeAsync(path);

            return info.Duration
                ?? throw new InvalidOperationException($"'{path}' has no readable duration.");
        }

        /// <summary>
        /// Whether the file has at least one audio stream. A video file isn't
        /// guaranteed to have one (e.g. a silent download) — the filter graph must
        /// not reference [idx:a] on a source that has no audio stream at all, or
        /// ffmpeg fails to bind the graph outright.
        /// </summary>
        public static async Task<bool> HasAudioStreamAsync(string path) =>
            (await ProbeAsync(path)).HasAudio;

        /// <summary>
        /// Synchronous ffprobe.
        ///
        /// stderr is drained on a background task while stdout is read on this
        /// thread. Reading them one after the other on a single thread can deadlock
        /// if the pipe the process is writing to fills up while nothing is
        /// consuming it.
        /// </summary>
        private static string RunFfprobe(params string[] args)
        {
            using var process = new Process { StartInfo = BuildStartInfo(args) };

            process.Start();

            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            string stdout = process.StandardOutput.ReadToEnd();

            process.WaitForExit();

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"ffprobe exited with code {process.ExitCode}:\n{stderrTask.GetAwaiter().GetResult()}");

            return stdout;
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