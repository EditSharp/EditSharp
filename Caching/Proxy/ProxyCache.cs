using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EditSharp.Video;

namespace EditSharp.Caching.Proxy
{
    /// <summary>Builds and finds proxies: one per original media file, used for scrubbing, playback and editing.</summary>
    /// <remarks>
    /// An original is identified by a hash of its content, so a renamed, moved
    /// or copied file finds the same proxy. Nothing builds automatically: call
    /// <see cref="BuildAsync"/> (typically on import) and watch
    /// <see cref="StatusChanged"/>; everything that reads media only looks
    /// proxies up. A proxy can be read up to <see cref="ProxyStatus.AvailableUpTo"/>
    /// while it builds, stays readable if the build is interrupted, and the next
    /// build resumes it. Building a different format replaces the proxy: the old
    /// one serves reads until the new one completes, then is deleted.
    /// <para>
    /// Proxies live under <see cref="EditSharpConfig.ProxyDirectory"/>, in a folder
    /// named after the first two characters of the hash. Each format has its own
    /// file stem (hash.delta7, .rgba, .dnxhr, .prores): .esrp proxies are one
    /// .esrp file, and MOV proxies are a .mov.json sidecar plus numbered segments
    /// while building and one .mov once complete.
    /// </para>
    /// </remarks>
    public static class ProxyCache
    {
        internal const int SchemaVersion = 2;

        private static readonly Time EventInterval = Time.FromMilliseconds(250);

        /// <summary>Raised when a file's proxy is queued, starts, progresses, finishes, fails or is cancelled.</summary>
        /// <remarks>Progress is reported at most four times a second per file. Raised on a background thread.</remarks>
        public static event EventHandler<ProxyStatusChangedEventArgs>? StatusChanged;

        private sealed class Job(string hash, string sourcePath, ProxyFormat format, HardwareAccelerator hwAccel)
        {
            public string Hash { get; } = hash;
            public string SourcePath { get; } = sourcePath;
            public ProxyFormat Format { get; } = format;
            public HardwareAccelerator HwAccel { get; } = hwAccel;
            public CancellationTokenSource Cts { get; } = new();
            public TaskCompletionSource<ProxyEntry> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public List<IProgress<double>> Progress { get; } = [];
            public int Waiters { get; set; }
            public ProxyStatus Status { get; set; }
            public long LastEventTimestamp { get; set; }
            public ProxyBuildPlan? Plan { get; set; }
        }

        //guards Jobs, Queue, _running and every Job's mutable state
        private static readonly object Gate = new();
        private static readonly Dictionary<string, Job> Jobs = new();
        private static readonly LinkedList<Job> Queue = new();
        private static int _running;

        private static readonly ConcurrentDictionary<string, ProxyEntry> Entries = new();
        private static readonly ConcurrentDictionary<string, ProxyStatus> Failures = new();
        private static readonly ConcurrentDictionary<(string Path, long Length, long LastWriteTicks), string> Hashes = new();


        /// <summary>Builds a file's proxy, resuming a partial one of the same format, and finishes when it's complete.</summary>
        /// <remarks>Finishes at once if a complete proxy in that format exists. Several callers asking for the same file share one build. Cancelling only detaches this caller; the build stops when no caller is left, and what it wrote stays readable and resumable.</remarks>
        /// <param name="sourcePath">The original media file.</param>
        /// <param name="format">The format to build; null for <see cref="EditSharpConfig.ProxyFormat"/>.</param>
        /// <param name="progress">Receives the build's progress, 0 to 1.</param>
        /// <param name="hwAccel">Whether decoding the original may use the GPU.</param>
        /// <param name="ct">Detaches this caller from the build.</param>
        /// <returns>The complete proxy.</returns>
        /// <exception cref="FileNotFoundException"><paramref name="sourcePath"/> doesn't exist.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
        public static async Task<ProxyEntry> BuildAsync(
            string sourcePath, ProxyFormat? format = null, IProgress<double>? progress = null,
            HardwareAccelerator hwAccel = HardwareAccelerator.GPU, CancellationToken ct = default)
        {
            ProxyFormat target = format ?? EditSharpConfig.ProxyFormat;
            string hash = await HashAsync(sourcePath, ct);

            while (true)
            {
                if (await LoadAsync(hash) is { } existing && existing.Format == target && ReadDiskStatus(existing).State == ProxyState.Complete)
                    return existing;

                Job job;
                Job? other = null;

                lock (Gate)
                {
                    if (Jobs.TryGetValue(hash, out Job? running) && running.Format != target)
                    {
                        other = running;
                        job = null!;
                    }
                    else
                    {
                        job = running ?? StartOrQueue(hash, sourcePath, target, hwAccel);
                        job.Waiters++;
                        if (progress is not null) job.Progress.Add(progress);
                    }
                }

                //a build of another format is under way: let it finish (or fail), then replace it
                if (other is not null)
                {
                    try { await other.Done.Task.WaitAsync(ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { }
                    continue;
                }

                using CancellationTokenRegistration detach = ct.Register(() => Detach(job, progress));
                return await job.Done.Task.WaitAsync(ct);
            }
        }

        /// <summary>Where a file's proxy stands, from running builds first and then the disk.</summary>
        /// <param name="sourcePath">The original media file.</param>
        /// <param name="ct">Cancels hashing the file.</param>
        /// <returns>The proxy's status; <see cref="ProxyStatus.NotCached"/> when there is none.</returns>
        /// <exception cref="FileNotFoundException"><paramref name="sourcePath"/> doesn't exist.</exception>
        public static async Task<ProxyStatus> GetStatusAsync(string sourcePath, CancellationToken ct = default)
        {
            string hash = await HashAsync(sourcePath, ct);

            lock (Gate)
            {
                if (Jobs.TryGetValue(hash, out Job? job)) return job.Status;
            }

            if (await LoadAsync(hash) is { } entry)
            {
                ProxyStatus status = ReadDiskStatus(entry);
                return Failures.TryGetValue(hash, out ProxyStatus failed) ? failed with { AvailableUpTo = status.AvailableUpTo } : status;
            }

            return Failures.TryGetValue(hash, out ProxyStatus failure) ? failure : ProxyStatus.NotCached;
        }

        /// <summary>Finds a file's proxy on disk, complete or partial.</summary>
        /// <remarks>Afterwards <see cref="TryGetEntry"/> answers for the same unchanged file without waiting.</remarks>
        /// <param name="sourcePath">The original media file.</param>
        /// <param name="ct">Cancels hashing the file.</param>
        /// <returns>The proxy, or null when there is none.</returns>
        /// <exception cref="FileNotFoundException"><paramref name="sourcePath"/> doesn't exist.</exception>
        public static async Task<ProxyEntry?> TryGetAsync(string sourcePath, CancellationToken ct = default) =>
            await LoadAsync(await HashAsync(sourcePath, ct));

        /// <summary>A file's proxy, if this process already knows it; for per-frame callers that can't wait.</summary>
        /// <remarks>Known means found by <see cref="TryGetAsync"/>, <see cref="GetStatusAsync"/> or a build. It never hashes the file or reads the disk, so false only means not known yet.</remarks>
        /// <param name="sourcePath">The original media file.</param>
        /// <param name="entry">The proxy, when known.</param>
        /// <returns>Whether the proxy is known.</returns>
        public static bool TryGetEntry(string sourcePath, out ProxyEntry entry)
        {
            entry = null!;

            var file = new FileInfo(sourcePath);
            if (!file.Exists) return false;

            return Hashes.TryGetValue(IdentityOf(file), out string? hash) && Entries.TryGetValue(hash, out entry!);
        }

        /// <summary>Whether a build of a file's proxy is queued or running in this process.</summary>
        /// <remarks>Answers from memory without waiting; a file this process hasn't hashed yet reads as not building.</remarks>
        /// <param name="sourcePath">The original media file.</param>
        /// <returns>Whether a build is queued or running.</returns>
        public static bool IsBuilding(string sourcePath)
        {
            var file = new FileInfo(sourcePath);
            if (!file.Exists || !Hashes.TryGetValue(IdentityOf(file), out string? hash)) return false;

            lock (Gate) return Jobs.ContainsKey(hash);
        }

        // ---- queue ----

        //called under Gate
        private static Job StartOrQueue(string hash, string sourcePath, ProxyFormat format, HardwareAccelerator hwAccel)
        {
            var job = new Job(hash, sourcePath, format, hwAccel);
            Jobs[hash] = job;
            Failures.TryRemove(hash, out _);

            if (_running < EditSharpConfig.MaxConcurrentProxyBuilds)
            {
                _running++;
                _ = RunAsync(job);
            }
            else
            {
                Queue.AddLast(job);
                job.Status = new ProxyStatus(ProxyState.Queued, Time.Zero, format, 0, null);
                Raise(job, force: true);
            }

            return job;
        }

        //called under Gate, once a running build has ended
        private static void StartQueued()
        {
            while (_running < EditSharpConfig.MaxConcurrentProxyBuilds && Queue.First is { } next)
            {
                Queue.RemoveFirst();
                _running++;
                _ = RunAsync(next.Value);
            }
        }

        private static void Detach(Job job, IProgress<double>? progress)
        {
            lock (Gate)
            {
                if (progress is not null) job.Progress.Remove(progress);
                if (--job.Waiters > 0) return;

                //nobody is waiting any more: stop it (or never start it)
                if (Queue.Remove(job))
                {
                    Jobs.Remove(job.Hash);
                    job.Done.TrySetCanceled();
                    job.Status = ProxyStatus.NotCached;
                    Raise(job, force: true);
                    return;
                }

                job.Cts.Cancel();
            }
        }

        // ---- building ----

        private static async Task RunAsync(Job job)
        {
            await Task.Yield();

            try
            {
                SetStatus(job, new ProxyStatus(ProxyState.Building, Time.Zero, job.Format, 0, null), force: true);

                ProxyBuildPlan plan = await PlanAsync(job);
                lock (Gate) job.Plan = plan;

                string target = PathFor(job.Hash, job.Format);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                var entry = new ProxyEntry(job.Hash, job.Format, target, plan.Width, plan.Height, plan.FrameRate);

                //readers may use the growing proxy at once; unless it's replacing another format, which keeps serving until this completes
                bool replacing = await LoadAsync(job.Hash) is { } old && old.Format != job.Format;
                bool published = false;

                void OnFrames(int frames)
                {
                    if (!published && !replacing)
                    {
                        Entries[job.Hash] = entry;
                        published = true;
                    }

                    SetStatus(job, new ProxyStatus(
                        ProxyState.Building, Time.FromFrame(frames, plan.FrameRate), job.Format,
                        plan.TotalFrames == 0 ? 1 : Math.Min(1, frames / (double)plan.TotalFrames), null));
                }

                if (entry.IsEsrp)
                    await EsrpProxyBuilder.BuildAsync(plan, target, OnFrames, job.Cts.Token);
                else
                    await MovProxyBuilder.BuildAsync(plan, target, OnFrames, job.Cts.Token);

                Entries[job.Hash] = entry;
                DeleteOtherFormats(job.Hash, job.Format);

                ProxyStatus done = ReadDiskStatus(entry);
                SetStatus(job, done, force: true);
                Finish(job, () => job.Done.TrySetResult(entry));
            }
            catch (OperationCanceledException) when (job.Cts.IsCancellationRequested)
            {
                Time available = job.Status.AvailableUpTo;
                SetStatus(job, available > Time.Zero
                    ? new ProxyStatus(ProxyState.Partial, available, job.Format, job.Status.Progress, null)
                    : ProxyStatus.NotCached, force: true);
                Finish(job, () => job.Done.TrySetCanceled());
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogError($"ProxyCache: building a proxy for '{job.SourcePath}' failed: {ex}");

                var failed = new ProxyStatus(ProxyState.Failed, job.Status.AvailableUpTo, job.Format, job.Status.Progress, ex);
                Failures[job.Hash] = failed;
                SetStatus(job, failed, force: true);
                Finish(job, () => job.Done.TrySetException(ex));
            }
        }

        private static void Finish(Job job, Action complete)
        {
            lock (Gate)
            {
                Jobs.Remove(job.Hash);
                _running--;
                StartQueued();
            }

            job.Cts.Dispose();
            complete();
        }

        private static async Task<ProxyBuildPlan> PlanAsync(Job job)
        {
            MediaInfo info = await MediaProbe.ProbeCachedAsync(job.SourcePath);

            if (!info.HasVideo || info.IsStillImage)
                throw new InvalidOperationException($"'{job.SourcePath}' has no video to build a proxy from.");

            if (info.Duration is not { } duration || duration <= Time.Zero)
                throw new InvalidOperationException($"'{job.SourcePath}' has no readable duration.");

            if (info.FrameRate is not { } frameRate)
                throw new InvalidOperationException($"'{job.SourcePath}' has no readable frame rate.");

            (int width, int height) = FitSize(info.Width, info.Height, EditSharpConfig.ProxyMaxDimension);
            int totalFrames = Math.Max(1, (int)duration.ToFrame(frameRate, Rounding.Ceiling));

            return new ProxyBuildPlan(job.SourcePath, job.Hash, info, job.Format, width, height, frameRate, totalFrames, job.HwAccel);
        }

        /// <summary>Longest side capped at `maxDimension`, aspect kept, never upscaled; even sizes, which every encoder accepts.</summary>
        private static (int Width, int Height) FitSize(int width, int height, int maxDimension)
        {
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("Can't size a proxy for a source with no readable dimensions.");

            double fit = Math.Min(1.0, (double)maxDimension / Math.Max(width, height));
            return (Even(width * fit), Even(height * fit));

            static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);
        }

        // ---- status ----

        private static void SetStatus(Job job, ProxyStatus status, bool force = false)
        {
            IProgress<double>[] listeners;

            lock (Gate)
            {
                job.Status = status;
                listeners = [.. job.Progress];
            }

            foreach (IProgress<double> listener in listeners) listener.Report(status.Progress);
            Raise(job, force);
        }

        private static void Raise(Job job, bool force)
        {
            long now = Stopwatch.GetTimestamp();

            if (!force && Time.FromTimeSpan(Stopwatch.GetElapsedTime(job.LastEventTimestamp, now)) < EventInterval) return;
            job.LastEventTimestamp = now;

            try
            {
                StatusChanged?.Invoke(null, new ProxyStatusChangedEventArgs(job.SourcePath, job.Status));
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogWarning($"ProxyCache: a StatusChanged handler threw: {ex.Message}");
            }
        }

        /// <summary>What an entry on disk says about itself: complete, or partial up to how far.</summary>
        internal static ProxyStatus ReadDiskStatus(ProxyEntry entry)
        {
            try
            {
                if (entry.IsEsrp)
                {
                    using EsrpReader reader = EsrpReader.Open(entry.Path);
                    int total = reader.IsComplete ? Math.Max(1, reader.AvailableFrames) : Math.Max(1, CapacityOf(entry.Path));
                    return StatusOf(reader.IsComplete, reader.AvailableFrames, total, entry);
                }

                MovProxyMeta meta = MovProxyMeta.Load(entry.Path);
                return StatusOf(meta.Complete, meta.AvailableFrames, Math.Max(1, meta.TotalFrames), entry);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                return new ProxyStatus(ProxyState.Failed, Time.Zero, entry.Format, 0, ex);
            }

            static ProxyStatus StatusOf(bool complete, int frames, int total, ProxyEntry entry) => new(
                complete ? ProxyState.Complete : ProxyState.Partial,
                Time.FromFrame(frames, entry.FrameRate), entry.Format,
                complete ? 1 : Math.Min(1, frames / (double)total), null);

            static int CapacityOf(string path) => EsrpReader.ReadHeaderAndMeta(path).Header.Capacity;
        }

        // ---- identity and disk ----

        private static (string, long, long) IdentityOf(FileInfo file) =>
            (Path.GetFullPath(file.FullName).ToLowerInvariant(), file.Length, file.LastWriteTimeUtc.Ticks);

        private static async Task<string> HashAsync(string sourcePath, CancellationToken ct)
        {
            var file = new FileInfo(sourcePath);
            if (!file.Exists) throw new FileNotFoundException($"Media not found: '{sourcePath}'", sourcePath);

            var identity = IdentityOf(file);
            if (Hashes.TryGetValue(identity, out string? known)) return known;

            string hash = await MediaHasher.ComputeAsync(sourcePath, ct);
            Hashes[identity] = hash;
            return hash;
        }

        private static string DirectoryFor(string hash) => Path.Combine(EditSharpConfig.ProxyDirectory, hash[..2]);

        internal static string StemFor(string hash, ProxyFormat format) => format switch
        {
            ProxyFormat.EsrpDelta7 => $"{hash}.delta7",
            ProxyFormat.EsrpRgba => $"{hash}.rgba",
            ProxyFormat.DNxHR => $"{hash}.dnxhr",
            ProxyFormat.ProRes => $"{hash}.prores",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };

        private static string PathFor(string hash, ProxyFormat format) => format is ProxyFormat.EsrpDelta7 or ProxyFormat.EsrpRgba
            ? Path.Combine(DirectoryFor(hash), $"{StemFor(hash, format)}.esrp")
            : Path.Combine(DirectoryFor(hash), $"{StemFor(hash, format)}.mov.json");

        /// <summary>
        /// The entry for `hash`, from memory or else from disk. If both an .esrp
        /// and a MOV proxy exist (a replacement was interrupted), a complete one
        /// wins over a partial one, then the most recently written.
        /// </summary>
        private static Task<ProxyEntry?> LoadAsync(string hash) => Entries.TryGetValue(hash, out ProxyEntry? known)
            ? Task.FromResult<ProxyEntry?>(known)
            : Task.Run(() =>
            {
                var found = new List<(ProxyEntry Entry, bool Complete, DateTime Written)>();

                foreach (string esrp in new[] { PathFor(hash, ProxyFormat.EsrpDelta7), PathFor(hash, ProxyFormat.EsrpRgba) })
                {
                    if (!File.Exists(esrp)) continue;

                    try
                    {
                        (EsrpFormat.Header header, EsrpMeta meta) = EsrpReader.ReadHeaderAndMeta(esrp);
                        if (meta.SourceHash == hash && meta.SchemaVersion == SchemaVersion)
                        {
                            ProxyFormat format = header.PixelFormat == EsrpPixelFormat.IndexedDelta7 ? ProxyFormat.EsrpDelta7 : ProxyFormat.EsrpRgba;
                            found.Add((new ProxyEntry(hash, format, esrp, header.Width, header.Height, header.FrameRate),
                                header.Complete, File.GetLastWriteTimeUtc(esrp)));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException)
                    {
                        EditSharpConfig.Logger.LogVerbose($"ProxyCache: ignoring unreadable proxy '{esrp}': {ex.Message}");
                    }
                }

                foreach (string sidecar in new[] { PathFor(hash, ProxyFormat.DNxHR), PathFor(hash, ProxyFormat.ProRes) })
                {
                    if (!File.Exists(sidecar)) continue;

                    try
                    {
                        MovProxyMeta meta = MovProxyMeta.Load(sidecar);
                        if (meta.SourceHash == hash && meta.SchemaVersion == SchemaVersion)
                            found.Add((new ProxyEntry(hash, meta.Format, sidecar, meta.Width, meta.Height, meta.FrameRate),
                                meta.Complete, File.GetLastWriteTimeUtc(sidecar)));
                    }
                    catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
                    {
                        EditSharpConfig.Logger.LogVerbose($"ProxyCache: ignoring unreadable proxy '{sidecar}': {ex.Message}");
                    }
                }

                if (found.Count == 0) return null;

                found.Sort((a, b) => a.Complete != b.Complete ? b.Complete.CompareTo(a.Complete) : b.Written.CompareTo(a.Written));
                return (ProxyEntry?)Entries.GetOrAdd(hash, found[0].Entry);
            });

        private static void DeleteOtherFormats(string hash, ProxyFormat kept)
        {
            foreach (ProxyFormat format in Enum.GetValues<ProxyFormat>())
            {
                if (format == kept) continue;

                string path = PathFor(hash, format);
                if (!File.Exists(path)) continue;

                try
                {
                    if (format is ProxyFormat.EsrpDelta7 or ProxyFormat.EsrpRgba) File.Delete(path);
                    else MovProxyBuilder.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    EditSharpConfig.Logger.LogWarning($"ProxyCache: couldn't remove the replaced {format} proxy for {hash}: {ex.Message}");
                }
            }
        }
    }
}
