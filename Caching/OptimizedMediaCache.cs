using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EditSharp;
using EditSharp.Video;
 
namespace EditSharp.Caching
{
    /// <summary>
    /// Where a lookup or a build attempt currently stands for one source —
    /// meant for a consumer app's own UI (an "optimizing..." badge on an
    /// imported clip, a retry affordance on failure), not consumed
    /// internally by anything else in this pipeline.
    /// </summary>
    public enum OptimizedMediaStatus
    {
        NotCached,
        Building,
        Ready,
        Failed,
    }
 
    /// <summary>
    /// One source's ready-to-use optimized media: where it lives on disk,
    /// its own actual dimensions/duration (probed from the built file
    /// itself, not re-derived), whether the ORIGINAL source has audio (the
    /// optimized file itself never does — see OptimizedMediaCache.
    /// EncodeAsync), and which codec it was built with.
    /// </summary>
    public readonly record struct OptimizedMediaEntry(
        string Path, int Width, int Height, double DurationSeconds, bool HasAudio, VideoCodec Codec);
 
    /// <summary>
    /// Persistent, content-addressed cache of decode-friendly optimized
    /// media for video sources — DECIDED IN CONVERSATION as the playback/
    /// render rewrite's central new piece. Existing shape summarized here
    /// since this is a genuinely new subsystem, not a small edit to one:
    ///
    /// WHY THIS EXISTS, AND WHY IT'S DIFFERENT FROM THE OLD (REMOVED)
    /// OptimizedMediaBuilder: that class pre-rendered a CLIP's own
    /// PreTransform-effects-baked content once per RENDER, to a TEMP file
    /// deleted when the render finished — solving independent per-frame
    /// ffmpeg processes' need for fast random-access seeks. Item 13's
    /// rewrite removed that need entirely (SourceDecoder is one
    /// long-lived sequential pipe per clip; see its own remarks) and
    /// OptimizedMediaBuilder/OptimizedMediaEffectsBaker went unused as a
    /// result — both deleted as part of this change, per direct
    /// confirmation that they were free to be replaced.
    ///
    /// This cache exists for a different reason: an ARBITRARY SEEK
    /// (scrubbing, or Playback.Play(startPosition) restarting a session
    /// mid-timeline) still has to reopen a clip's decoder at a new offset
    /// into its SOURCE file, and a real-world source's own codec/GOP
    /// structure can make that reopen-and-seek expensive — especially for
    /// long-GOP delivery codecs never meant for scrubbing. DNxHR/ProRes are
    /// all-intra by construction: every frame is independently decodable,
    /// so a seek into either is just "start reading from here," the same
    /// property optimized media has always traded disk space and a
    /// one-time encode for.
    ///
    /// CONTENT-ADDRESSED, KEYED BY MediaHasher'S HASH — not by path. A
    /// source that gets renamed, moved to a different drive, or duplicated
    /// under a second name still resolves to the exact same cache entry,
    /// which is what makes this genuinely useful for an NLE-type consumer
    /// reopening the same project (possibly with its media relinked)
    /// across app runs, rather than only within one render's lifetime.
    ///
    /// ONE CANONICAL RESOLUTION PER SOURCE — DECIDED IN CONVERSATION: a
    /// source's optimized media is built once, at (native resolution,
    /// capped to EditSharpConfig.OptimizedMediaMaxDimension), never at a
    /// per-timeline or per-canvas size the way the old per-render
    /// OptimizedMediaBuilder was. There's no single "canvas size" this
    /// cache could size against anyway — the same source can appear across
    /// many timelines/projects at different scales. Callers that need MORE
    /// resolution than the cap (a clip zoomed in past it) are expected to
    /// fall back to the true original source rather than upscale from a
    /// smaller proxy — see ContentPreparation.ProbeVideoAsync's own
    /// per-clip sufficiency check, which is where that comparison actually
    /// happens; this class has no per-clip knowledge to make that call
    /// itself.
    ///
    /// CPU ENCODE/DECODE ONLY, DELIBERATELY — DECIDED IN CONVERSATION:
    /// Vulkan Video's (and every other vendor's) hardware codec blocks only
    /// cover H.264/HEVC/AV1; DNxHR and ProRes have never had hardware
    /// ASIC support anywhere; they're designed to be fast SOFTWARE codecs
    /// instead. GpuContext/D3D12 continue to own compositing exactly as
    /// before — this cache's own encode (BuildAsync) and every decode of
    /// its output (see ContentPreparation forcing
    /// DecodeHwAccelPlan.Software for a cache hit) simply never attempt a
    /// hardware path that doesn't exist.
    ///
    /// FULLY OPPORTUNISTIC AT THE CALL SITE, NEVER AUTO-BUILDING: nothing
    /// in Playback or Renderer triggers a build on a cache miss — see
    /// ContentPreparation.ProbeVideoAsync's own remarks for why
    /// (building competes for CPU/disk with the very playback it would be
    /// trying to keep smooth). Populating the cache ahead of need is an
    /// explicit, opt-in action a consumer app takes by calling
    /// PrewarmAsync — typically right after import, exactly matching "an
    /// app can begin optimized media generation as soon as a file is
    /// imported" from the original conversation.
    ///
    /// TWO LAYERS OF IN-PROCESS MEMOIZATION, BOTH ADDED AFTER DIRECT
    /// FEEDBACK THAT REPEAT LOOKUPS FOR THE SAME UNCHANGED SOURCE WERE
    /// STILL COSTING REAL TIME (a single test video observed hitting
    /// TryGetAsync six times in one session, each paying real hash/disk
    /// cost rather than being free after the first):
    ///
    ///   1. SourceResolutionCache — keyed on the ORIGINAL source's own
    ///      (normalized path, length, LastWriteTimeUtc), the exact same
    ///      identity MediaHasher already uses to decide whether a file
    ///      needs rehashing at all. A confirmed resolution for that exact
    ///      file skips BOTH the hash computation AND the meta.json read —
    ///      the fastest possible repeat lookup, and the one that actually
    ///      matters for a Playback session, since every call site
    ///      (RefreshScrubbingSupportAsync, a Play() session's own
    ///      PrepareContentAsync, a scrub session's base setup) independently
    ///      calls TryGetAsync for the same source without knowing whether
    ///      another call site already resolved it moments earlier.
    ///   2. ResolvedEntries — keyed on (content hash, codec), the fallback
    ///      for when the source's own file identity isn't known ahead of
    ///      time (or SourceResolutionCache was bypassed) but its content
    ///      hash already is.
    ///
    /// Both memoize only POSITIVE hits, never misses — a miss can
    /// legitimately turn into a hit later (a build completes, a prewarm
    /// finishes), and caching that would need its own invalidation story
    /// neither of these carries.
    /// </summary>
    public static class OptimizedMediaCache
    {
        private const int CurrentSchemaVersion = 1;
 
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };
 
        // Coalesces concurrent requests for the SAME content hash into one
        // real build — e.g. a PrewarmAsync call right after import and a
        // Playback session starting moments later against the same source
        // share the one build in flight rather than each starting their
        // own redundant ffmpeg process. Entries are removed once the build
        // finishes (success OR failure) — see BuildAndTrackAsync — so this
        // is a coalescing mechanism for IN-FLIGHT work only, not a second
        // cache layered on top of the disk cache TryLoadExistingAsync
        // already reads from.
        private static readonly ConcurrentDictionary<string, Task<OptimizedMediaEntry>> InFlight = new();
 
        // Remembers the most recent failure per content hash, purely so
        // GetStatusAsync can report OptimizedMediaStatus.Failed for a
        // consumer app's UI (a retry affordance) rather than the failure
        // silently vanishing the instant InFlight removes its entry.
        // Cleared the moment a LATER attempt for the same hash succeeds.
        private static readonly ConcurrentDictionary<string, Exception> LastFailure = new();
 
        // See the class remarks' memoization section, layer 2 — a
        // confirmed-good (hash, codec) -> entry mapping, cached forever for
        // this process's lifetime once first resolved.
        private static readonly ConcurrentDictionary<(string Hash, VideoCodec Codec), OptimizedMediaEntry>
            ResolvedEntries = new();
 
        // See the class remarks' memoization section, layer 1 — a
        // confirmed-good (original source path, length, mtime) -> entry
        // mapping. This is the one that actually collapses repeat
        // TryGetAsync calls for the SAME FILE down to a dictionary lookup,
        // skipping MediaHasher.ComputeAsync entirely, not just the disk
        // read layer 2 skips.
        private static readonly ConcurrentDictionary<(string Path, long Length, long LastWriteTimeUtcTicks), OptimizedMediaEntry>
            SourceResolutionCache = new();
 
        /// <summary>
        /// Returns ready-to-use optimized media for `sourcePath`, building
        /// it first if no cache entry exists yet — this BLOCKS the caller
        /// until the build finishes (or fails). Intended for a caller that
        /// has already decided it wants this source optimized and is
        /// willing to wait, e.g. PrewarmAsync itself, or a batch job.
        /// Playback/Renderer do NOT call this — see the class remarks on
        /// why they use TryGetAsync instead, never triggering a build of
        /// their own.
        /// </summary>
        public static async Task<OptimizedMediaEntry> GetOrBuildAsync(
            string sourcePath, CancellationToken ct = default)
        {
            OptimizedMediaEntry? existing = await TryGetAsync(sourcePath, ct);
            if (existing != null) return existing.Value;
 
            string hash = await MediaHasher.ComputeAsync(sourcePath, ct);
 
            Task<OptimizedMediaEntry> build = InFlight.GetOrAdd(
                hash, _ => BuildAndTrackAsync(sourcePath, hash));
 
            // .WaitAsync(ct) lets THIS caller's own cancellation stop
            // waiting without cancelling the shared build another caller
            // (or a caller with no token at all) may also be awaiting —
            // the build itself runs to completion regardless once started,
            // matching every other ffmpeg-process helper in this codebase,
            // none of which support mid-process cancellation either.
            return await build.WaitAsync(ct);
        }
 
        /// <summary>
        /// Starts building optimized media for `sourcePath` ahead of need
        /// and returns immediately-awaitable Task for it — the "end users
        /// can prompt generation whenever they want" entry point from the
        /// original conversation. A consumer app typically calls this
        /// right after import, or whenever it wants to get ahead of
        /// playback for a source it knows is coming up. Safe to call
        /// without awaiting the result at all (fire-and-forget) — errors
        /// still surface later via GetStatusAsync rather than an unobserved
        /// exception, since BuildAndTrackAsync records failures into
        /// LastFailure before rethrowing.
        ///
        /// A no-op (returns the already-completed entry) if this source's
        /// content is already cached; coalesces with any build already in
        /// flight for the same content rather than starting a redundant
        /// second one — both inherited from GetOrBuildAsync, which this
        /// simply forwards to.
        /// </summary>
        public static Task<OptimizedMediaEntry> PrewarmAsync(string sourcePath, CancellationToken ct = default) =>
            GetOrBuildAsync(sourcePath, ct);
 
        /// <summary>
        /// Non-blocking lookup: returns an existing cache entry for
        /// `sourcePath` if one is already built and on disk, or null
        /// otherwise — NEVER starts a build. This is what Playback and
        /// Renderer actually call (via ContentPreparation) so a
        /// cache miss costs nothing beyond the hash itself, not an
        /// unexpected multi-second (or longer) encode blocking whatever
        /// asked.
        ///
        /// Checks SourceResolutionCache FIRST, keyed on `sourcePath`'s own
        /// current (length, LastWriteTimeUtc) — a hit there skips
        /// MediaHasher entirely, not just the disk read (see the class
        /// remarks' memoization section). Only a cache MISS at that layer
        /// falls through to actually hashing the file and consulting
        /// ResolvedEntries/disk.
        /// </summary>
        public static async Task<OptimizedMediaEntry?> TryGetAsync(
            string sourcePath, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
 
            var info = new FileInfo(sourcePath);
            if (info.Exists)
            {
                var sourceKey = (NormalizeSourcePath(sourcePath), info.Length, info.LastWriteTimeUtc.Ticks);
 
                if (SourceResolutionCache.TryGetValue(sourceKey, out OptimizedMediaEntry memoized))
                {
                    EditSharpConfig.Logger.LogVerbose(
                        $"OptimizedMediaCache.TryGetAsync('{sourcePath}'): source-identity cache hit, " +
                        $"{sw.ElapsedMilliseconds}ms (no hash, no disk read).");
                    return memoized;
                }
            }
 
            string hash = await MediaHasher.ComputeAsync(sourcePath, ct);
            TimeSpan hashElapsed = sw.Elapsed;
 
            OptimizedMediaEntry? result = await TryLoadExistingAsync(hash);
 
            if (result != null && info.Exists)
            {
                var sourceKey = (NormalizeSourcePath(sourcePath), info.Length, info.LastWriteTimeUtc.Ticks);
                SourceResolutionCache[sourceKey] = result.Value;
            }
 
            // TEMPORARY INSTRUMENTATION — added to directly measure how
            // much of a Play()/ScrubToAsync session-prep call's time this
            // cache lookup itself accounts for, after direct feedback that
            // startup remained slower than the pre-cache baseline, and that
            // the SAME source was being looked up several times in one
            // session. Same "log at LogVerbose, remove once the question is
            // answered" convention SourceDecoder.NextFrame already uses.
            EditSharpConfig.Logger.LogVerbose(
                $"OptimizedMediaCache.TryGetAsync('{sourcePath}'): hash {hashElapsed.TotalMilliseconds:F0}ms, " +
                $"total {sw.ElapsedMilliseconds}ms ({(result != null ? "hit, now memoized" : "miss")}).");
 
            return result;
        }
 
        /// <summary>
        /// Where a lookup/build for `sourcePath` currently stands — see
        /// OptimizedMediaStatus. Meant for a consumer app's own UI, not
        /// consumed anywhere else in this pipeline.
        /// </summary>
        public static async Task<OptimizedMediaStatus> GetStatusAsync(
            string sourcePath, CancellationToken ct = default)
        {
            OptimizedMediaEntry? existing = await TryGetAsync(sourcePath, ct);
            if (existing != null) return OptimizedMediaStatus.Ready;
 
            string hash = await MediaHasher.ComputeAsync(sourcePath, ct);
 
            if (InFlight.TryGetValue(hash, out Task<OptimizedMediaEntry>? task))
                return task.IsFaulted ? OptimizedMediaStatus.Failed : OptimizedMediaStatus.Building;
 
            if (LastFailure.ContainsKey(hash))
                return OptimizedMediaStatus.Failed;
 
            return OptimizedMediaStatus.NotCached;
        }
 
        private static async Task<OptimizedMediaEntry> BuildAndTrackAsync(string sourcePath, string hash)
        {
            try
            {
                OptimizedMediaEntry entry = await BuildAsync(sourcePath, hash);
                LastFailure.TryRemove(hash, out _);
                ResolvedEntries[(hash, entry.Codec)] = entry;
 
                var info = new FileInfo(sourcePath);
                if (info.Exists)
                    SourceResolutionCache[(NormalizeSourcePath(sourcePath), info.Length, info.LastWriteTimeUtc.Ticks)] = entry;
 
                return entry;
            }
            catch (Exception ex)
            {
                LastFailure[hash] = ex;
                throw;
            }
            finally
            {
                // Don't keep a finished build's Task cached here forever —
                // a FAILED build must be retryable on a later call rather
                // than permanently rethrowing the same captured exception,
                // and even a SUCCEEDED one should stop being served from
                // here once it's safely on disk, so TryLoadExistingAsync's
                // disk check becomes the source of truth again rather than
                // this dictionary silently pinning every build's Task (and
                // its captured closures) in memory for the process's life.
                InFlight.TryRemove(hash, out _);
            }
        }
 
        /// <summary>
        /// The actual ffmpeg encode: probes the source, computes the
        /// capped target size, re-encodes to EditSharpConfig.
        /// OptimizedMediaCodec, and lands it (plus its companion
        /// .meta.json) at its final hash-addressed path — see the class
        /// remarks for why this file is where it is and looks the way it
        /// does.
        /// </summary>
        private static async Task<OptimizedMediaEntry> BuildAsync(string sourcePath, string hash)
        {
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException(
                    $"Cannot build optimized media — source not found: '{sourcePath}'", sourcePath);
 
            MediaInfo sourceInfo = await MediaProbe.ProbeAsync(sourcePath);
 
            if (!sourceInfo.HasVideo)
                throw new InvalidOperationException($"'{sourcePath}' has no video stream to optimize.");
 
            (int width, int height) = ComputeTargetSize(
                sourceInfo.Width, sourceInfo.Height, EditSharpConfig.OptimizedMediaMaxDimension);
 
            VideoCodec codec = EditSharpConfig.OptimizedMediaCodec;
 
            (string finalMediaPath, string finalMetaPath, string dir) = PathsFor(hash, codec);
            Directory.CreateDirectory(dir);
 
            // BUG FIX: this used to be `finalMediaPath + $".tmp-{guid}"`,
            // which put the temp suffix AFTER the real extension —
            // "...db61.mov.tmp-41a9ca64...". ffmpeg picks an output muxer
            // by sniffing the OUTPUT FILENAME'S extension when no `-f` is
            // given, and ".tmp-41a9ca64..." isn't a recognized one, so
            // muxer selection failed outright (ffmpeg exit -22, "Unable to
            // choose an output format"). Keeping the real extension LAST
            // (hash.tmp-GUID.ext instead of hash.ext.tmp-GUID) is what lets
            // ffmpeg's own extension-based format sniffing keep working,
            // while still writing to a name that can never collide with
            // the final path until the rename below.
            string extension = VideoUtils.ContainerExtensionFor(codec);
            string tempMediaPath = Path.Combine(dir, $"{hash}.tmp-{Guid.NewGuid():N}.{extension}");
 
            EditSharpConfig.Logger.Log(
                $"Building optimized media for '{sourcePath}' ({width}x{height}, {codec})...");
            var sw = Stopwatch.StartNew();
 
            try
            {
                await EncodeAsync(sourcePath, tempMediaPath, codec, width, height);
 
                // Atomic on the same volume — a concurrent reader (another
                // process, or TryLoadExistingAsync in THIS process racing
                // a build it didn't know was already in flight — see
                // GetOrBuildAsync coalescing on InFlight, which prevents
                // that within one process but not across two) only ever
                // observes either "not there yet" or "fully written",
                // never a half-encoded file.
                File.Move(tempMediaPath, finalMediaPath, overwrite: true);
            }
            catch
            {
                TryDelete(tempMediaPath);
                throw;
            }
 
            MediaInfo builtInfo = await MediaProbe.ProbeAsync(finalMediaPath);
 
            var meta = new OptimizedMediaMeta
            {
                SchemaVersion = CurrentSchemaVersion,
                SourceHash = hash,
                KnownSourcePaths = { Path.GetFullPath(sourcePath) },
                OriginalWidth = sourceInfo.Width,
                OriginalHeight = sourceInfo.Height,
                OriginalDurationSeconds = sourceInfo.Duration?.TotalSeconds,
                OriginalHasAudio = sourceInfo.HasAudio,
                Codec = codec,
                Width = builtInfo.Width,
                Height = builtInfo.Height,
                DurationSeconds = builtInfo.Duration?.TotalSeconds,
                MaxDimensionAtBuild = EditSharpConfig.OptimizedMediaMaxDimension,
                CreatedAtUtc = DateTime.UtcNow,
            };
 
            await WriteMetaAsync(finalMetaPath, meta);
 
            EditSharpConfig.Logger.Log(
                $"Optimized media built in {sw.ElapsedMilliseconds}ms -> {finalMediaPath}");
 
            return new OptimizedMediaEntry(
                finalMediaPath, builtInfo.Width, builtInfo.Height,
                builtInfo.Duration?.TotalSeconds ?? sourceInfo.Duration?.TotalSeconds ?? 0,
                sourceInfo.HasAudio, codec);
        }
 
        /// <summary>
        /// Looks up an existing, already-built entry for `hash` on disk —
        /// the read side of the cache. Returns null for anything short of
        /// "both files present and the meta parses cleanly and agrees with
        /// what's being asked for" — a corrupt or stale entry is treated as
        /// a plain miss (silently rebuildable via GetOrBuildAsync) rather
        /// than an error, since nothing about a half-written or outdated
        /// cache entry should ever be fatal to the caller that stumbled
        /// onto it.
        ///
        /// A confirmed HIT is memoized into ResolvedEntries before
        /// returning — see the class remarks' memoization section, layer
        /// 2. A miss is deliberately NOT memoized here.
        /// </summary>
        private static async Task<OptimizedMediaEntry?> TryLoadExistingAsync(string hash)
        {
            VideoCodec codec = EditSharpConfig.OptimizedMediaCodec;
 
            if (ResolvedEntries.TryGetValue((hash, codec), out OptimizedMediaEntry memoized))
                return memoized;
 
            (string mediaPath, string metaPath, _) = PathsFor(hash, codec);
 
            if (!File.Exists(mediaPath) || !File.Exists(metaPath))
                return null;
 
            OptimizedMediaMeta? meta;
            try
            {
                string json = await File.ReadAllTextAsync(metaPath);
                meta = JsonSerializer.Deserialize<OptimizedMediaMeta>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogVerbose(
                    $"OptimizedMediaCache: meta for '{hash}' unreadable, treating as a miss: {ex.Message}");
                return null;
            }
 
            if (meta == null || meta.SchemaVersion != CurrentSchemaVersion || meta.SourceHash != hash)
            {
                EditSharpConfig.Logger.LogVerbose(
                    $"OptimizedMediaCache: meta for '{hash}' is stale or mismatched, treating as a miss.");
                return null;
            }
 
            if (meta.Codec != codec)
            {
                // The configured codec changed since this entry was built
                // (e.g. DNxHR -> ProRes). Both codecs currently share the
                // same ".mov" container extension (see VideoUtils.
                // ContainerExtensionFor), so `mediaPath` can genuinely
                // exist here while holding the OLD codec's bytes under the
                // new codec's expected filename — meta.Codec is what
                // catches that, since the filename alone can't. Treated as
                // a miss; a later GetOrBuildAsync overwrites this same
                // path with the newly configured codec's own encode.
                return null;
            }
 
            var entry = new OptimizedMediaEntry(
                mediaPath, meta.Width, meta.Height,
                meta.DurationSeconds ?? 0, meta.OriginalHasAudio, meta.Codec);
 
            ResolvedEntries[(hash, codec)] = entry;
            return entry;
        }
 
        /// <summary>
        /// The target size to build a source's optimized media at: native
        /// resolution capped to `maxDimension` on its longest axis,
        /// aspect-preserved, never upscaled past native — same reasoning
        /// the old (removed) OptimizedMediaBuilder.ComputeOptimizedMediaSize
        /// used for its own per-clip box, adapted to a single fixed cap
        /// since this cache has no per-clip/per-canvas context to size
        /// against (see the class remarks).
        ///
        /// Always rounds to EVEN dimensions on both axes, even when no
        /// scaling is needed at all: yuv422p/yuv422p10le (this cache's
        /// pixel formats — see VideoUtils.PixelFormatFor) are 4:2:2, which
        /// subsamples chroma horizontally and rejects an odd width/height
        /// outright. A source with odd native dimensions would otherwise
        /// fail to encode even when it's already under the cap and no
        /// resize was conceptually needed.
        /// </summary>
        private static (int Width, int Height) ComputeTargetSize(
            int nativeWidth, int nativeHeight, int maxDimension)
        {
            if (nativeWidth <= 0 || nativeHeight <= 0)
                throw new InvalidOperationException(
                    "Cannot compute optimized media size for a source with no readable dimensions.");
 
            int largest = Math.Max(nativeWidth, nativeHeight);
            double fit = largest <= maxDimension ? 1.0 : (double)maxDimension / largest;
 
            int width = RoundToEven(Math.Max(2, (int)Math.Round(nativeWidth * fit)));
            int height = RoundToEven(Math.Max(2, (int)Math.Round(nativeHeight * fit)));
 
            return (width, height);
        }
 
        private static int RoundToEven(int value) => value % 2 == 0 ? value : value + 1;
 
        private static (string MediaPath, string MetaPath, string Directory) PathsFor(string hash, VideoCodec codec)
        {
            // Sharded by the hash's own first two hex characters (256 even
            // buckets) so the cache folder never accumulates thousands of
            // files in one directory as a project library grows — a
            // standard content-addressable-storage layout, not specific to
            // this codebase.
            string shard = hash[..2];
            string dir = Path.Combine(EditSharpConfig.OptimizedMediaDirectory, shard);
 
            string extension = VideoUtils.ContainerExtensionFor(codec);
            string mediaPath = Path.Combine(dir, $"{hash}.{extension}");
            string metaPath = Path.Combine(dir, $"{hash}.meta.json");
 
            return (mediaPath, metaPath, dir);
        }
 
        private static async Task WriteMetaAsync(string finalPath, OptimizedMediaMeta meta)
        {
            // No extension-sniffing concern here (this is JSON text, not
            // handed to ffmpeg), but keeping the extension last anyway —
            // "{hash}.meta.json.tmp-GUID" — for the same reason it now
            // matters for the media file: consistency, and so nothing here
            // silently depends on ffmpeg's tolerance for a weird name.
            string tempPath = Path.Combine(
                Path.GetDirectoryName(finalPath) ?? "",
                $"{Path.GetFileName(finalPath)}.tmp-{Guid.NewGuid():N}");
            string json = JsonSerializer.Serialize(meta, JsonOptions);
 
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, finalPath, overwrite: true);
        }
 
        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best-effort cleanup of a failed build's partial temp file */ }
        }
 
        // Same case-normalization MediaHasher.NormalizePath uses (Windows
        // paths are case-insensitive) — kept as an independent copy rather
        // than exposed from MediaHasher, since the two caches' key shapes
        // only coincidentally share this one piece, not because this class
        // depends on MediaHasher's own internals.
        private static string NormalizeSourcePath(string path) => Path.GetFullPath(path).ToLowerInvariant();
 
        /// <summary>
        /// The actual ffmpeg re-encode to `codec` at `width`x`height`. No
        /// -ss/-t trim of any kind — this always encodes the WHOLE source
        /// file start to finish, deliberately, since the resulting entry
        /// is shared across every clip and every timeline that might ever
        /// reference this source's content, each with its own independent
        /// trim range. A per-clip trim would need a per-clip cache entry,
        /// which is exactly the per-canvas/per-clip sizing this cache was
        /// designed NOT to need (see the class remarks) — every consumer
        /// of the resulting file applies its own trim/seek offset against
        /// this file's own timeline exactly as it would against the
        /// original (see ContentPreparation.ProbeVideoAsync and
        /// ClipContentSource.GetOrOpenDecoder, both of which compute a
        /// seek offset in SECONDS FROM FILE START — identical math whether
        /// that file is the original or this cache entry, since both cover
        /// the source's full duration on the same timebase).
        ///
        /// No muxer cluster/seek tuning the way the old FFV1-in-matroska
        /// optimized media needed (see VideoUtils.MuxerTuningArgsFor's own
        /// remarks on -cluster_time_limit): mov/mp4's sample tables
        /// (stts/stsc/stsz/stco, and stss for sync samples) are built
        /// per-SAMPLE regardless of GOP size, and DNxHR/ProRes are
        /// all-intra (every sample IS a sync sample), so a -ss seek into
        /// this file is already frame-accurate with no extra encode-time
        /// work required.
        ///
        /// +FASTSTART, ADDED AFTER DIRECT FEEDBACK THAT SCRUBBING WAS AS
        /// SLOW AS A FRESH SESSION START: mov/mp4 muxers write the moov
        /// atom (the file's own seek index — sample tables, durations,
        /// stream layout) AFTER the media data by default, at the very END
        /// of the file. Every fresh decoder open against this file has to
        /// locate and read that atom before it can seek anywhere at all.
        /// Without this flag that costs a real extra seek-and-read on the
        /// END of the file on EVERY open, regardless of what position was
        /// actually requested — directly undercutting the entire premise
        /// that "opening at frame 40,000 costs the same as opening at
        /// frame 0." -movflags +faststart makes ffmpeg do one extra cheap
        /// remux pass at BUILD time to move the moov atom to the FRONT of
        /// the file instead, so every later open pays a small fixed cost
        /// instead of an end-of-file read. A build made before this fix
        /// does not retroactively gain it — delete
        /// EditSharpConfig.OptimizedMediaDirectory (or bump
        /// CurrentSchemaVersion) to force a rebuild if this matters for an
        /// already-populated cache.
        ///
        /// EXPLICIT -f, NOT LEFT TO EXTENSION SNIFFING: passed regardless
        /// of what outputPath looks like, so this never again depends on
        /// the caller having picked a filename ffmpeg can guess a muxer
        /// from — see BuildAsync's own remarks on the temp-filename bug
        /// this closes for real, and the belt-and-suspenders reasoning for
        /// not relying on filename sniffing a second time.
        /// </summary>
        private static async Task EncodeAsync(
            string sourcePath, string outputPath, VideoCodec codec, int width, int height)
        {
            if (!CodecNames.VideoCodecNames.TryGetValue(codec, out string? encoderName))
                throw new NotSupportedException($"OptimizedMediaCache has no encoder mapping for {codec}.");
 
            var args = new List<string> { "-y", "-v", "error" };
            args.AddRange(FfmpegArgs.FilterThreadingArgs());
 
            args.Add("-i");
            args.Add(sourcePath);
 
            args.Add("-vf");
            args.Add($"scale={width}:{height},setsar=1");
 
            args.Add("-c:v");
            args.Add(encoderName);
            args.AddRange(VideoUtils.ProfileArgsFor(codec));
 
            string? pixelFormat = VideoUtils.PixelFormatFor(codec);
            if (pixelFormat != null)
            {
                args.Add("-pix_fmt");
                args.Add(pixelFormat);
            }
 
            // Video only — audio for playback/render is always read from
            // the ORIGINAL source file directly (PlaybackAudioEngine,
            // AudioMixer), never from optimized media, exactly as the old
            // FFV1 optimized media never carried audio either.
            args.Add("-an");
 
            string containerFormat = VideoUtils.ContainerFormatNameFor(codec);
 
            args.Add("-f");
            args.Add(containerFormat);
 
            if (containerFormat is "mov" or "mp4")
            {
                // See this method's own remarks — moves the seek index to
                // the front of the file so every later open (every scrub
                // tick/reopen) is cheap, not just the first one.
                args.Add("-movflags");
                args.Add("+faststart");
            }
 
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
                    $"ffmpeg exited with code {process.ExitCode} building optimized media " +
                    $"for '{sourcePath}':\n{stderr}");
        }
    }
}
 