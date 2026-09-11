using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EditSharp.Caching.ScrubProxy
{
    /// <summary>
    /// Everything a scrub-proxy lookup needs to know about an entry beyond
    /// what's already in the .esrp file's own fixed header (Width/Height/
    /// SampleRate/FrameCount/CompressionScheme — see ScrubProxyFormat),
    /// plus diagnostic context for a human poking around the cache folder.
    ///
    /// EMBEDDED DIRECTLY IN THE .esrp FILE AS A UTF8 JSON BLOB — NO MORE
    /// SEPARATE .meta.json COMPANION FILE (decided in conversation, part of
    /// the v2 format — see ScrubProxyFormat's own VERSION 2 remarks for the
    /// full reasoning). ScrubProxyMetaSerializer (below) is the only thing
    /// that (de)serializes this type; ScrubProxyReader.Open/ReadHeaderAndMeta
    /// and ScrubProxyCache.EncodeAsync are its only callers.
    ///
    /// SourceHash is duplicated here even though it's already encoded in the
    /// entry's own filename — defence-in-depth: a filename can be renamed/
    /// copied outside this process without the contents changing, and
    /// re-checking the hash INSIDE the file against what the filename
    /// claims is what catches that (see ScrubProxyCache.TryLoadExistingAsync).
    ///
    /// Width/Height/SampleRate/FrameCount are deliberately NOT duplicated
    /// here any more (v1's ScrubProxyMeta had its own copies of these,
    /// needed back when they lived in a separate file from the header they
    /// had to match) — now that there is exactly one file, the fixed header
    /// is the single source of truth for those four values and nothing
    /// else needs to agree with it.
    /// </summary>
    internal sealed class ScrubProxyMeta
    {
        /// <summary>
        /// Bump whenever this shape, or ScrubProxyCache's own build
        /// conventions, change in a way that makes an OLDER entry on disk
        /// no longer trustworthy. A mismatch means "rebuild," not "fail to
        /// parse" — see ScrubProxyCache.CurrentSchemaVersion. Independent
        /// of ScrubProxyFormat.CurrentVersion (the container/file-shape
        /// version) — this one is about the MEANING of the fields below,
        /// not the bytes they're stored in.
        /// </summary>
        public int SchemaVersion { get; set; } = 1;

        public string SourceHash { get; set; } = "";

        /// <summary>
        /// Diagnostic only — NOT used to decide a cache hit. Every distinct
        /// path this hash has ever been built from gets appended, not just
        /// the most recent one.
        /// </summary>
        public List<string> KnownSourcePaths { get; set; } = new();

        public int OriginalWidth { get; set; }
        public int OriginalHeight { get; set; }

        /// <summary>
        /// EditSharpConfig.ScrubProxyTargetShortSide at the time this entry
        /// was built — diagnostic only. Deliberately NOT used to force a
        /// rebuild when the configured target changes: an entry built at a
        /// different short side is still valid, usable data.
        /// </summary>
        public int TargetShortSideAtBuild { get; set; }

        public DateTime CreatedAtUtc { get; set; }
    }

    /// <summary>
    /// (De)serializes ScrubProxyMeta to/from the UTF8 JSON bytes embedded in
    /// a .esrp file — the one place that shape is turned into bytes and
    /// back, so ScrubProxyReader (read side) and ScrubProxyCache (write
    /// side) can never drift on JSON options between them.
    /// </summary>
    internal static class ScrubProxyMetaSerializer
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };

        public static byte[] SerializeToUtf8Bytes(ScrubProxyMeta meta) =>
            JsonSerializer.SerializeToUtf8Bytes(meta, JsonOptions);

        /// <summary>
        /// Parses an embedded metadata blob. Never returns null — throws
        /// InvalidDataException (a plain "treat as a miss" signal, same
        /// spirit as ScrubProxyFormat.ReadHeader's own throws) on an empty,
        /// malformed, or null-deserializing blob, rather than handing a
        /// caller a half-valid ScrubProxyMeta to reason about.
        /// </summary>
        public static ScrubProxyMeta Deserialize(ReadOnlySpan<byte> utf8Json, string diagnosticPath)
        {
            if (utf8Json.Length == 0)
                throw new InvalidDataException($"'{diagnosticPath}' has an empty embedded metadata blob.");

            try
            {
                return JsonSerializer.Deserialize<ScrubProxyMeta>(utf8Json, JsonOptions)
                    ?? throw new InvalidDataException($"'{diagnosticPath}' has a null embedded metadata blob.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"'{diagnosticPath}' has an unreadable embedded metadata blob: {ex.Message}", ex);
            }
        }
    }
}