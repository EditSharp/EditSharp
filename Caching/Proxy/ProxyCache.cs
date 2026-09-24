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
    /// <summary>
    /// The one proxy per original media file; shared by scrubbing, playback
    /// and paused editing alike. An entry remembers its original (by content
    /// hash, so a renamed, moved or duplicated file finds the same proxy), its
    /// format, and how far it has been built.
    ///
    /// NOTHING BUILDS AUTOMATICALLY. A consumer calls BuildAsync (typically
    /// on import) and watches StatusChanged; everything that reads media only
    /// ever looks proxies up. Builds queue behind
    /// EditSharpConfig.MaxConcurrentProxyBuilds; concurrent requests for the
    /// same file share one build, which is cancelled only once every caller
    /// has cancelled.
    ///
    /// PARTIAL PROXIES ARE FIRST-CLASS: a proxy is readable from its start up
    /// to AvailableUpTo while it builds, stays readable if the build is
    /// interrupted (ProxyState.Partial), and the next BuildAsync resumes from
    /// there. Building a different format than the one on disk replaces it:
    /// the old proxy keeps serving reads until the new one completes, then is
    /// deleted.
    ///
    /// ON DISK (EditSharpConfig.ProxyDirectory/&lt;hash[..2]&gt;/), one stem per
    /// format (&lt;hash&gt;.delta7, .rgba, .dnxhr, .prores) so a replacement never
    /// collides with the proxy it replaces:
    ///   &lt;stem&gt;.esrp                 an .esrp proxy (progress lives in its index)
    ///   &lt;stem&gt;.mov.json             a MOV proxy's sidecar (segments/progress)
    ///   &lt;stem&gt;.seg&lt;n&gt;.mov, &lt;stem&gt;.mov   its segments while building, its final file once complete
    /// </summary>
    public static class ProxyCache
    {
        internal const int SchemaVersion = 1;

        private static readonly TimeSpan EventInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Raised whenever a file's proxy status changes; queued, started,
        /// progressed (at most ~4 times a second per file), finished, failed or
        /// cancelled. Raised on a background thread.
        /// </summary>
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

        // ---------------------------------------------------------------
        // Public API
        // ---------------------------------------------------------------

        /// <summary>
        /// Builds `sourcePath`'s proxy in `format` (EditSharpConfig.ProxyFormat
        /// when null), resuming a partial one of the same format, and returns
        /// once it's complete. Returns at once if a complete proxy in that
        /// format already exists. Cancelling only detaches this caller; the
        /// build itself stops when no caller is left waiting on it, and what it
        /// wrote stays usable and resumable.
        /// </summary>
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

        /// <summary>Where `sourcePath`'s proxy stands, looking at running builds first and then the disk.</summary>
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

        /// <summary>
        /// `sourcePath`'s proxy (complete or partial) if one is on disk, loading
        /// what's needed to find it. After this, TryGetEntry answers
        /// synchronously for the same unchanged file.
        /// </summary>
        public static async Task<ProxyEntry?> TryGetAsync(string sourcePath, CancellationToken ct = default) =>
            await LoadAsync(await HashAsync(sourcePath, ct));

        /// <summary>
        /// Synchronous, memory-only lookup for per-frame callers: the proxy for
        /// `sourcePath` if this process already knows it (via TryGetAsync, a
        /// status query, or a build). Never hashes and never reads the cache
        /// from disk; a miss only means "not known yet".
        /// </summary>
        public static bool TryGetEntry(string sourcePath, out ProxyEntry entry)
        {
            entry = null!;

            var file = new FileInfo(sourcePath);
            if (!file.Exists) return false;

            return Hashes.TryGetValue(IdentityOf(file), out string? hash) && Entries.TryGetValue(hash, out entry!);
        }

        /// <summary>
        /// Synchronous, memory-only: whether a build for `sourcePath` is queued
        /// or running in this process right now.
        /// </summary>
        public static bool IsBuilding(string sourcePath)
        {
            var file = new FileInfo(sourcePath);
            if (!file.Exists || !Hashes.TryGetValue(IdentityOf(file), out string? hash)) return false;

            lock (Gate) return Jobs.ContainsKey(hash);
        }

        // ---------------------------------------------------------------
        // Queue
        // ---------------------------------------------------------------

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
                job.Status = new ProxyStatus(ProxyState.Queued, TimeSpan.Zero, format, 0, null);
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

        // ---------------------------------------------------------------
        // Building
        // ---------------------------------------------------------------

        private static async Task RunAsync(Job job)
        {
            await Task.Yield();

            try
            {
                SetStatus(job, new ProxyStatus(ProxyState.Building, TimeSpan.Zero, job.Format, 0, null), force: true);

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
                        ProxyState.Building, TimeSpan.FromSeconds(frames / plan.FrameRate), job.Format,
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
                TimeSpan available = job.Status.AvailableUpTo;
                SetStatus(job, available > TimeSpan.Zero
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

            if (info.Duration is not { } duration || duration <= TimeSpan.Zero)
                throw new InvalidOperationException($"'{job.SourcePath}' has no readable duration.");

            if (info.FrameRate is not { } frameRate)
                throw new InvalidOperationException($"'{job.SourcePath}' has no readable frame rate.");

            (int width, int height) = FitSize(info.Width, info.Height, EditSharpConfig.ProxyMaxDimension);
            int totalFrames = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds * frameRate));

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

        // ---------------------------------------------------------------
        // Status
        // ---------------------------------------------------------------

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

            if (!force && Stopwatch.GetElapsedTime(job.LastEventTimestamp, now) < EventInterval) return;
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
                return new ProxyStatus(ProxyState.Failed, TimeSpan.Zero, entry.Format, 0, ex);
            }

            static ProxyStatus StatusOf(bool complete, int frames, int total, ProxyEntry entry) => new(
                complete ? ProxyState.Complete : ProxyState.Partial,
                TimeSpan.FromSeconds(frames / entry.FrameRate), entry.Format,
                complete ? 1 : Math.Min(1, frames / (double)total), null);

            static int CapacityOf(string path) => EsrpReader.ReadHeaderAndMeta(path).Header.Capacity;
        }

        // ---------------------------------------------------------------
        // Identity and disk
        // ---------------------------------------------------------------

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
