using System;
using System.Collections.Generic;

namespace EditSharp.Render
{
    /// <summary>
    /// The companion .meta.json file written alongside every persistent
    /// optimized-media entry — everything a later lookup needs to know
    /// about the entry WITHOUT re-probing the media file itself (a second
    /// ffprobe call per lookup would partly defeat the point of caching in
    /// the first place), plus everything a consumer app might want to show
    /// about a piece of optimized media (native resolution, duration,
    /// codec) without opening the file itself.
    ///
    /// SourceHash is duplicated here even though it's already encoded in
    /// the entry's own filename — not redundant, defence in depth: a
    /// filename can be renamed or copied by something outside this process
    /// (a user poking around the cache folder, a backup/sync tool) without
    /// the contents changing. Re-checking the hash INSIDE the file against
    /// the hash the filename claims is what catches that, rather than
    /// trusting the filename blindly — see OptimizedMediaCache.
    /// TryLoadExistingAsync.
    /// </summary>
    internal sealed class OptimizedMediaMeta
    {
        /// <summary>
        /// Bump whenever this shape, or the encoding conventions
        /// OptimizedMediaCache builds entries with, change in a way that
        /// makes an OLDER entry on disk no longer trustworthy. A mismatch
        /// here means "rebuild", not "fail to parse" — see
        /// OptimizedMediaCache.CurrentSchemaVersion.
        /// </summary>
        public int SchemaVersion { get; set; } = 1;

        public string SourceHash { get; set; } = "";

        /// <summary>
        /// Diagnostic only — NOT used to decide a cache hit (the folder/
        /// filename lookup by hash already is that), but useful for a
        /// human poking around the cache folder to see which real file(s)
        /// a given hash corresponds to. Every distinct path this hash has
        /// ever been built from gets appended here, not just the most
        /// recent one — see OptimizedMediaCache.BuildAsync.
        /// </summary>
        public List<string> KnownSourcePaths { get; set; } = new();

        public int OriginalWidth { get; set; }
        public int OriginalHeight { get; set; }
        public double? OriginalDurationSeconds { get; set; }
        public bool OriginalHasAudio { get; set; }

        public VideoCodec Codec { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double? DurationSeconds { get; set; }

        /// <summary>
        /// EditSharpConfig.OptimizedMediaMaxDimension at the time this
        /// entry was built — diagnostic only, same as KnownSourcePaths.
        /// Deliberately NOT used to force a rebuild when the configured cap
        /// changes: a smaller-than-current-cap entry is still valid data,
        /// just not as large as a fresh build would be. Whether an existing
        /// entry is actually big ENOUGH for a given use is decided
        /// per-request at the call site (see RenderContentPreparation.
        /// ProbeVideoAsync comparing Width/Height against the clip's own
        /// required content size), not here.
        /// </summary>
        public int MaxDimensionAtBuild { get; set; }

        public DateTime CreatedAtUtc { get; set; }
    }
}