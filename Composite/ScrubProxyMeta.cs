using System;
using System.Collections.Generic;

namespace EditSharp.Composite
{
    /// <summary>
    /// The companion .meta.json file written alongside every persistent
    /// scrub-proxy entry — same purpose and shape as OptimizedMediaMeta:
    /// everything a later lookup needs to know about the entry without
    /// re-probing the source or re-reading the .esrp file's own header, plus
    /// diagnostic context for a human poking around the cache folder.
    ///
    /// SourceHash is duplicated here even though it's already encoded in the
    /// entry's own filename — same defence-in-depth reasoning
    /// OptimizedMediaMeta documents: a filename can be renamed/copied
    /// outside this process without the contents changing, and re-checking
    /// the hash INSIDE the file against what the filename claims is what
    /// catches that (see ScrubProxyCache.TryLoadExistingAsync).
    /// </summary>
    internal sealed class ScrubProxyMeta
    {
        /// <summary>
        /// Bump whenever this shape, or ScrubProxyCache's own build
        /// conventions, change in a way that makes an OLDER entry on disk
        /// no longer trustworthy. A mismatch means "rebuild," not "fail to
        /// parse" — see ScrubProxyCache.CurrentSchemaVersion.
        /// </summary>
        public int SchemaVersion { get; set; } = 1;

        public string SourceHash { get; set; } = "";

        /// <summary>
        /// Diagnostic only, same as OptimizedMediaMeta.KnownSourcePaths —
        /// NOT used to decide a cache hit. Every distinct path this hash has
        /// ever been built from gets appended, not just the most recent one.
        /// </summary>
        public List<string> KnownSourcePaths { get; set; } = new();

        public int OriginalWidth { get; set; }
        public int OriginalHeight { get; set; }

        public int Width { get; set; }
        public int Height { get; set; }

        /// <summary>Samples per second this entry was built at — must match the .esrp file's own header exactly.</summary>
        public double SampleRate { get; set; }

        public int FrameCount { get; set; }

        /// <summary>
        /// EditSharpConfig.ScrubProxyTargetShortSide at the time this entry
        /// was built — diagnostic only, same as OptimizedMediaMeta.
        /// MaxDimensionAtBuild. Deliberately NOT used to force a rebuild
        /// when the configured target changes: an entry built at a
        /// different short side is still valid, usable data.
        /// </summary>
        public int TargetShortSideAtBuild { get; set; }

        public DateTime CreatedAtUtc { get; set; }
    }
}