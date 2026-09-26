using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EditSharp.Video;

namespace EditSharp.Caching.Proxy
{
    /// <summary>One fragmented-MOV piece of a DNxHR/ProRes proxy: `Frames` frames starting at original frame `StartFrame`.</summary>
    internal sealed class MovSegment
    {
        public string File { get; set; } = "";
        public int StartFrame { get; set; }
        public int Frames { get; set; }
    }

    /// <summary>
    /// The sidecar JSON of a DNxHR/ProRes proxy; a MOV can't carry its own
    /// progress, so this does. While building (or after an interruption) it
    /// lists the segments written so far; once complete, just the final file.
    /// </summary>
    internal sealed class MovProxyMeta
    {
        public int SchemaVersion { get; set; } = ProxyCache.SchemaVersion;
        public string SourceHash { get; set; } = "";
        public List<string> KnownSourcePaths { get; set; } = new();
        public ProxyFormat Format { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public Rational FrameRate { get; set; }
        public int TotalFrames { get; set; }
        public List<MovSegment> Segments { get; set; } = new();
        public bool Complete { get; set; }
        public string? FinalFile { get; set; }
        public DateTime CreatedAtUtc { get; set; }

        [JsonIgnore]
        public int AvailableFrames => Complete ? TotalFrames : Segments.Sum(s => s.Frames);

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };

        public static MovProxyMeta Load(string sidecarPath) =>
            JsonSerializer.Deserialize<MovProxyMeta>(File.ReadAllBytes(sidecarPath), JsonOptions)
            ?? throw new InvalidDataException($"'{sidecarPath}' is empty.");

        /// <summary>Written to a temp file and moved over, so a reader never sees half a sidecar.</summary>
        public void Save(string sidecarPath)
        {
            string temp = $"{sidecarPath}.tmp-{Guid.NewGuid():N}";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions));
            File.Move(temp, sidecarPath, overwrite: true);
        }

        /// <summary>
        /// Which file holds original frame `frame`, and at what time within
        /// that file; or null if it hasn't been written. File names in the
        /// sidecar are relative to its directory.
        /// </summary>
        public (string File, Time Time)? Locate(int frame, string directory)
        {
            if (frame < 0) return null;

            if (Complete)
                return frame < TotalFrames && FinalFile is not null
                    ? (Path.Combine(directory, FinalFile), Time.FromFrame(frame, FrameRate))
                    : null;

            foreach (MovSegment segment in Segments)
            {
                if (frame >= segment.StartFrame && frame < segment.StartFrame + segment.Frames)
                    return (Path.Combine(directory, segment.File), Time.FromFrame(frame - segment.StartFrame, FrameRate));
            }

            return null;
        }
    }

    /// <summary>
    /// Builds (or resumes) a DNxHR/ProRes proxy as a run of fragmented-MOV
    /// segments; fragmented so each is decodable while still being written,
    /// segmented because ffmpeg can't append to an existing MOV: a resumed
    /// build simply starts a new segment where the recorded frames end. The
    /// sidecar's frame counts trail the encoder by one fragment (a fragment
    /// becomes readable only once it's closed), so they only ever claim
    /// frames that are really on disk. When every frame is written the
    /// segments are stream-copied into one faststart MOV (no re-encode; each
    /// cut at its recorded count) and deleted.
    /// </summary>
    internal static class MovProxyBuilder
    {
        private static readonly Time FragmentDuration = Time.FromSeconds(1);
        private static readonly Time SaveInterval = Time.FromSeconds(1);

        public static VideoCodec CodecFor(ProxyFormat format) => format switch
        {
            ProxyFormat.DNxHR => VideoCodec.DNxHR,
            ProxyFormat.ProRes => VideoCodec.ProRes,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not a MOV proxy format."),
        };

        public static async Task BuildAsync(ProxyBuildPlan plan, string sidecarPath, Action<int> onFramesWritten, CancellationToken ct)
        {
            string directory = Path.GetDirectoryName(sidecarPath)!;
            string stem = ProxyCache.StemFor(plan.Hash, plan.Format);
            MovProxyMeta meta = TryResume(plan, sidecarPath) ?? Fresh(plan, sidecarPath);

            onFramesWritten(meta.AvailableFrames);

            int start = meta.AvailableFrames;
            if (start < meta.TotalFrames)
            {
                var segment = new MovSegment { File = $"{stem}.seg{meta.Segments.Count}.mov", StartFrame = start };
                meta.Segments.Add(segment);
                meta.Save(sidecarPath);

                await EncodeSegmentAsync(plan, meta, segment, Path.Combine(directory, segment.File), sidecarPath, onFramesWritten, ct);
            }

            await FinishAsync(plan, meta, sidecarPath, directory);
            onFramesWritten(meta.TotalFrames);
        }

        private static MovProxyMeta? TryResume(ProxyBuildPlan plan, string sidecarPath)
        {
            if (!File.Exists(sidecarPath)) return null;

            try
            {
                MovProxyMeta meta = MovProxyMeta.Load(sidecarPath);

                bool matches = meta.SourceHash == plan.Hash && meta.SchemaVersion == ProxyCache.SchemaVersion &&
                    !meta.Complete && meta.Format == plan.Format && meta.Width == plan.Width &&
                    meta.Height == plan.Height && meta.FrameRate == plan.FrameRate;

                if (matches)
                {
                    //a segment that never recorded a frame is just noise
                    string directory = Path.GetDirectoryName(sidecarPath)!;
                    foreach (MovSegment empty in meta.Segments.Where(s => s.Frames == 0).ToList())
                    {
                        TryDelete(Path.Combine(directory, empty.File));
                        meta.Segments.Remove(empty);
                    }

                    EditSharpConfig.Logger.Log($"Resuming proxy for '{plan.SourcePath}' at frame {meta.AvailableFrames}/{meta.TotalFrames}.");
                    return meta;
                }
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogWarning($"Can't resume the proxy at '{sidecarPath}', starting over: {ex.Message}");
            }

            Delete(sidecarPath);
            return null;
        }

        private static MovProxyMeta Fresh(ProxyBuildPlan plan, string sidecarPath)
        {
            EditSharpConfig.Logger.Log(
                $"Building {plan.Format} proxy for '{plan.SourcePath}' ({plan.Width}x{plan.Height} @ {plan.FrameRate}fps, " +
                $"{plan.TotalFrames} frames).");

            var meta = new MovProxyMeta
            {
                SourceHash = plan.Hash,
                KnownSourcePaths = { Path.GetFullPath(plan.SourcePath) },
                Format = plan.Format,
                Width = plan.Width,
                Height = plan.Height,
                FrameRate = plan.FrameRate,
                TotalFrames = plan.TotalFrames,
                CreatedAtUtc = DateTime.UtcNow,
            };

            meta.Save(sidecarPath);
            return meta;
        }

        private static async Task EncodeSegmentAsync(
            ProxyBuildPlan plan, MovProxyMeta meta, MovSegment segment, string output, string sidecarPath,
            Action<int> onFramesWritten, CancellationToken ct)
        {
            VideoCodec codec = CodecFor(plan.Format);
            string encoder = CodecNames.VideoCodecNames[codec];
            string rate = FfmpegArgs.Rate(plan.FrameRate);
            int lag = (int)FragmentDuration.ToFrame(plan.FrameRate, Rounding.Ceiling);

            var args = new List<string> { "-y", "-v", "error", "-nostats", "-progress", "pipe:1" };
            args.AddRange(FfmpegArgs.FilterThreadingArgs());

            if (segment.StartFrame > 0)
            {
                //decoding re-encode, so the seek is frame-accurate
                args.Add("-ss");
                args.Add(FfmpegArgs.Sec(Time.FromFrame(segment.StartFrame, plan.FrameRate)));
            }

            args.AddRange(["-i", plan.SourcePath, "-vf", $"fps={rate},scale={plan.Width}:{plan.Height},setsar=1"]);
            args.AddRange(["-frames:v", (meta.TotalFrames - segment.StartFrame).ToString(CultureInfo.InvariantCulture)]);
            args.AddRange(["-c:v", encoder]);
            args.AddRange(VideoUtils.ProfileArgsFor(codec));

            if (VideoUtils.PixelFormatFor(codec) is { } pixelFormat)
                args.AddRange(["-pix_fmt", pixelFormat]);

            //audio always comes from the original
            args.Add("-an");
            args.AddRange(["-f", "mov", "-movflags", "+frag_keyframe+empty_moov+default_base_moof"]);
            args.AddRange(["-frag_duration", Time.MulDiv(FragmentDuration.Ticks, 1_000_000, Time.TicksPerSecond, Rounding.Nearest).ToString(CultureInfo.InvariantCulture)]);
            args.Add(output);

            var psi = new ProcessStartInfo
            {
                FileName = EditSharpConfig.FfmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi };
            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginErrorReadLine();

            using CancellationTokenRegistration kill = ct.Register(static state =>
            {
                var p = (Process)state!;
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }, process);

            var sinceSave = Stopwatch.StartNew();

            //-progress writes key=value blocks; "frame=" is how many frames the encoder has taken
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
            {
                if (!line.StartsWith("frame=", StringComparison.Ordinal) ||
                    !int.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out int encoded))
                    continue;

                int readable = Math.Max(0, encoded - lag);
                if (readable <= segment.Frames) continue;

                segment.Frames = readable;
                onFramesWritten(meta.AvailableFrames);

                if (Time.FromTimeSpan(sinceSave.Elapsed) >= SaveInterval)
                {
                    meta.Save(sidecarPath);
                    sinceSave.Restart();
                }
            }

            await process.WaitForExitAsync(CancellationToken.None);

            if (ct.IsCancellationRequested)
            {
                meta.Save(sidecarPath);
                ct.ThrowIfCancellationRequested();
            }

            if (process.ExitCode != 0)
            {
                meta.Save(sidecarPath);
                throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode} building a proxy for '{plan.SourcePath}':\n{stderr}");
            }

            //a clean exit closes the last fragment: everything encoded is on disk. The
            //source may hold fewer frames than its container claimed.
            segment.Frames = await CountFramesAsync(output, plan.FrameRate) is { } counted && counted > 0
                ? counted
                : segment.Frames;
            meta.TotalFrames = segment.StartFrame + segment.Frames;
            meta.Save(sidecarPath);
        }

        private static async Task<int?> CountFramesAsync(string file, Rational frameRate)
        {
            Time? duration = (await MediaProbe.ProbeAsync(file)).Duration;
            return duration is { } d ? (int)d.ToFrame(frameRate, Rounding.Nearest) : null;
        }

        /// <summary>Stream-copies the segments (each cut at its recorded frames) into one faststart MOV and drops them.</summary>
        private static async Task FinishAsync(ProxyBuildPlan plan, MovProxyMeta meta, string sidecarPath, string directory)
        {
            string stem = ProxyCache.StemFor(plan.Hash, plan.Format);
            string finalName = $"{stem}.mov";
            string finalPath = Path.Combine(directory, finalName);
            string listPath = Path.Combine(directory, $"{stem}.concat-{Guid.NewGuid():N}.txt");

            var list = new StringBuilder();
            foreach (MovSegment segment in meta.Segments)
            {
                list.Append("file '").Append(segment.File.Replace("'", @"'\''")).Append("'\n");
                list.Append("outpoint ").Append(FfmpegArgs.Sec(Time.FromFrame(segment.Frames, plan.FrameRate))).Append('\n');
            }

            await File.WriteAllTextAsync(listPath, list.ToString());

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = EditSharpConfig.FfmpegPath,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                foreach (string arg in new[] { "-y", "-v", "error", "-f", "concat", "-safe", "0", "-i", listPath, "-c", "copy", "-movflags", "+faststart", finalPath })
                    psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi)!;
                string stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode} finishing the proxy for '{plan.SourcePath}':\n{stderr}");
            }
            finally
            {
                TryDelete(listPath);
            }

            List<MovSegment> segments = meta.Segments.ToList();

            meta.TotalFrames = meta.AvailableFrames;
            meta.Complete = true;
            meta.FinalFile = finalName;
            meta.Segments.Clear();
            meta.Save(sidecarPath);

            foreach (MovSegment segment in segments) TryDelete(Path.Combine(directory, segment.File));

            EditSharpConfig.Logger.Log($"Proxy complete for '{plan.SourcePath}' -> {finalPath}");
        }

        /// <summary>Removes a MOV proxy entirely: sidecar, segments and final file.</summary>
        public static void Delete(string sidecarPath)
        {
            string directory = Path.GetDirectoryName(sidecarPath)!;

            try
            {
                MovProxyMeta meta = MovProxyMeta.Load(sidecarPath);
                foreach (MovSegment segment in meta.Segments) TryDelete(Path.Combine(directory, segment.File));
                if (meta.FinalFile is not null) TryDelete(Path.Combine(directory, meta.FinalFile));
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
            {
            }

            TryDelete(sidecarPath);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
