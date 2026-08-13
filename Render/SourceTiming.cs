using System;
using System.Threading.Tasks;
using EditSharp.Components;

namespace EditSharp.Render
{
    /// <summary>
    /// Answers "how long is this source?" — both the raw file length and the
    /// usable length once Source.Start and Source.Duration are taken into account.
    ///
    /// Public because it is as useful when building a timeline as it is inside the
    /// pipeline: setting a clip's Duration to match its media is the common case,
    /// and doing that by hand means duplicating the Start/Duration rules and the
    /// clamping below.
    ///
    /// Every call here spawns an ffprobe process, so hold on to the result rather
    /// than calling it repeatedly for the same file in a loop.
    /// </summary>
    public static class SourceTiming
    {
        /// <summary>
        /// The full length of the underlying file, ignoring Start and Duration.
        ///
        /// Images have no inherent length — a PNG is not "3 seconds long" — so
        /// asking for one is an error rather than a guess. Use UsableLengthAsync,
        /// which returns an image's explicitly configured Duration.
        /// </summary>
        public static async Task<TimeSpan> FullLengthAsync(Source source)
        {
            GuardNotImage(source);

            return (await MediaProbe.ProbeAsync(source.Path)).Duration
                ?? throw new InvalidOperationException($"'{source.Path}' has no readable duration.");
        }

        /// <summary>Blocking equivalent of FullLengthAsync.</summary>
        public static TimeSpan FullLength(Source source)
        {
            GuardNotImage(source);

            return MediaProbe.Probe(source.Path).Duration
                ?? throw new InvalidOperationException($"'{source.Path}' has no readable duration.");
        }

        private static void GuardNotImage(Source source)
        {
            if (source.Type == SourceType.Image)
                throw new InvalidOperationException(
                    $"Source '{source.Path}' is an Image, which has no inherent length. " +
                    "Set Source.Duration explicitly and read it back through UsableLength.");
        }

        /// <summary>
        /// How much material the source actually offers, once Start and Duration
        /// are applied.
        ///
        /// An explicit Duration is honoured but CLAMPED to what is really there —
        /// asking for 10 seconds starting 8 seconds into a 12 second file yields 4,
        /// not 10. Without the clamp the trim would simply run out of frames and
        /// hand back a stream shorter than the caller was told to expect, which
        /// then desynchronizes everything computed from it downstream.
        /// </summary>
        public static async Task<TimeSpan> UsableLengthAsync(Source source)
        {
            var (_, length) = await ResolveRangeAsync(source);
            return length;
        }

        /// <summary>
        /// Blocking equivalent of UsableLengthAsync, for authoring code with no
        /// async context to await in.
        ///
        /// Launches an ffprobe process and waits for it, so prefer the async form
        /// wherever there is somewhere to await. If the file's length is already
        /// known, use the overload that takes it and avoid probing entirely.
        /// </summary>
        public static TimeSpan UsableLength(Source source) => ResolveRange(source).Length;

        /// <summary>
        /// The usable length against a file length the caller already knows. No
        /// process is launched, so this is the one to reach for inside a loop.
        /// fileLength is ignored for images and may be null for them.
        /// </summary>
        public static TimeSpan UsableLength(Source source, TimeSpan? fileLength) =>
            ResolveRange(source, fileLength).Length;

        /// <summary>
        /// The concrete start offset and length to read from the source.
        ///
        /// A null Start means the beginning; a null Duration means everything left
        /// after Start. This is the single place those defaults are applied, so the
        /// pipeline and any calling code agree on what a half-specified Source
        /// means.
        /// </summary>
        public static async Task<(TimeSpan Start, TimeSpan Length)> ResolveRangeAsync(Source source)
        {
            //probing here costs a process, so callers that already have the file's
            //length should use the synchronous overload instead
            TimeSpan? fileLength = source.Type == SourceType.Image
                ? null
                : (await MediaProbe.ProbeAsync(source.Path)).Duration;

            return ResolveRange(source, fileLength);
        }

        /// <summary>
        /// Blocking equivalent of ResolveRangeAsync. Same caveat: it launches a
        /// process, so prefer the async form where one can be awaited.
        /// </summary>
        public static (TimeSpan Start, TimeSpan Length) ResolveRange(Source source)
        {
            TimeSpan? fileLength = source.Type == SourceType.Image
                ? null
                : MediaProbe.Probe(source.Path).Duration;

            return ResolveRange(source, fileLength);
        }

        /// <summary>
        /// The same resolution against a file length the caller already knows.
        /// Exists so the pipeline, which probes each source once up front, does not
        /// spawn a second ffprobe just to reapply the Start/Duration rules.
        /// fileLength is ignored for images and may be null for them.
        /// </summary>
        public static (TimeSpan Start, TimeSpan Length) ResolveRange(Source source, TimeSpan? fileLength)
        {
            TimeSpan start = source.Start ?? TimeSpan.Zero;

            if (start < TimeSpan.Zero)
                throw new InvalidOperationException(
                    $"Source '{source.Path}': Start ({start}) is negative.");

            if (source.Type == SourceType.Image)
            {
                //an image holds a single frame for as long as it is asked to, so its
                //length is whatever was configured and nothing can be probed
                if (!source.Duration.HasValue)
                    throw new InvalidOperationException(
                        $"Source '{source.Path}' is an Image with a null Duration. Images have " +
                        "no inherent length, so Duration must be set explicitly.");

                return (TimeSpan.Zero, source.Duration.Value);
            }

            if (fileLength is not { } length)
                throw new InvalidOperationException($"'{source.Path}' has no readable duration.");

            TimeSpan remaining = length - start;

            if (remaining <= TimeSpan.Zero)
                throw new InvalidOperationException(
                    $"Source '{source.Path}': Start ({start}) is at or past the end of the " +
                    $"file ({length}).");

            TimeSpan usable = source.Duration.HasValue && source.Duration.Value < remaining
                ? source.Duration.Value
                : remaining;

            return (start, usable);
        }
    }
}
