using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EditSharp;
using EditSharp.Components;
using EditSharp.Components.Clips;
using EditSharp.Components.Effects;
 
namespace EditSharp.Composite
{
    /// <summary>
    /// Everything about a video clip's MEDIA INPUTS that's constant across
    /// the clip's whole life — probed once up front, reused every frame.
    ///
    /// "CLIPS ARE GRAPHS" REWRITE — this class shrank a lot:
    ///   - Keyed by INPUT NODE Id (Guid), not by Clip. A VideoClip's graph
    ///     can now contain any number of MediaSourceNodes, each with its own
    ///     native size / decode plan / decode source path — there's no
    ///     longer a single "the clip's source" to key a Dictionary&lt;Clip,...&gt;
    ///     by.
    ///   - TextInputNode/ColorGeneratorInputNode/NoiseInputNode need NO prep
    ///     here at all any more. A TextInputNode is rasterized lazily and
    ///     cached by SkClipContentSource itself (which also owns deleting its
    ///     own temp PNG on Dispose) instead of being prepared up front into a
    ///     tempFiles bag this class used to own — nothing here needs the
    ///     canvas size to precompute a native size for those any more either,
    ///     since TransformNode now derives "native size" directly from
    ///     whatever image is ACTUALLY upstream of it at evaluation time (see
    ///     EffectGraphEvaluatorSk's own remarks), not from a value this class
    ///     precomputed.
    ///   - A MediaSourceNode with Source.Type == Image similarly needs no
    ///     prep: SkClipContentSource just loads it directly and caches the
    ///     decoded SKImage in memory, with no path lookup required.
    ///   - The ONLY thing that genuinely still benefits from being probed
    ///     once, up front, is a Video-type MediaSourceNode: its native
    ///     dimensions (needed to size an ffmpeg decode target and to judge
    ///     OptimizedMediaCache sufficiency) and which file its decoder
    ///     should actually open.
    /// </summary>
    internal static class RenderContentPreparation
    {
        public static Task PrepareContentAsync(
            Timeline timeline, int canvasWidth, int canvasHeight, HardwareAccelerator hwAccel,
            ConcurrentDictionary<Guid, (int, int)> nativeSizes,
            ConcurrentDictionary<Guid, DecodeHwAccelPlan> decodePlans,
            ConcurrentDictionary<Guid, string> decodeSourcePaths)
        {
            var tasks = new List<Task>();
 
            foreach (Channel channel in timeline.Channels)
            {
                foreach (Clip clip in channel.Clips)
                {
                    if (clip is not VideoClip video) continue;
 
                    foreach (MediaSourceNode media in video.Graph.Nodes.OfType<MediaSourceNode>())
                    {
                        if (media.Source.Type != SourceType.Video) continue;
 
                        tasks.Add(ProbeVideoAsync(
                            video, media, canvasWidth, canvasHeight, hwAccel,
                            nativeSizes, decodePlans, decodeSourcePaths));
                    }
                }
            }
 
            return Task.WhenAll(tasks);
        }
 
        /// <summary>
        /// Probes one MediaSourceNode's ORIGINAL source for its native size
        /// (needed for aspect-fit math regardless of which file ends up
        /// actually decoded), then decides which file
        /// SkClipContentSource.GetOrOpenDecoder should actually open — see
        /// this class's own remarks.
        /// </summary>
        private static async Task ProbeVideoAsync(
            VideoClip clip, MediaSourceNode media, int canvasWidth, int canvasHeight, HardwareAccelerator hwAccel,
            ConcurrentDictionary<Guid, (int, int)> nativeSizes,
            ConcurrentDictionary<Guid, DecodeHwAccelPlan> decodePlans,
            ConcurrentDictionary<Guid, string> decodeSourcePaths)
        {
            (int width, int height) = await MediaProbe.GetDimensionsAsync(media.Source.Path);
            nativeSizes[media.Id] = (width, height);
 
            string? cachedPath = await TryGetSufficientCachedMediaAsync(
                clip, media, media.Source.Path, width, height, canvasWidth, canvasHeight);
 
            string decodeSourcePath = cachedPath ?? media.Source.Path;
            decodeSourcePaths[media.Id] = decodeSourcePath;
 
            decodePlans[media.Id] = decodeSourcePath == media.Source.Path
                ? await FfmpegRunner.GetDecodePlanAsync(media.Source.Path, hwAccel)
                : DecodeHwAccelPlan.Software;
        }
 
        /// <summary>
        /// The shared cache-sufficiency check: looks up an OPPORTUNISTIC
        /// (never-building) cached entry for `sourcePath`, and returns its
        /// Path only when it has enough resolution for this MediaSourceNode's
        /// own largest on-screen size. Returns null on a cache miss, an
        /// insufficient entry, or any lookup failure — callers fall back to
        /// the original source in every one of those cases.
        ///
        /// The "required size" is computed against the first TransformNode
        /// reachable downstream of `media` — see GraphSearchHelpers' own
        /// remarks on why that's an approximation for a graph with more than
        /// one TransformNode, and exact for every "normal/default" graph.
        /// </summary>
        private static async Task<string?> TryGetSufficientCachedMediaAsync(
            VideoClip clip, MediaSourceNode media, string sourcePath, int nativeWidth, int nativeHeight,
            int canvasWidth, int canvasHeight)
        {
            try
            {
                OptimizedMediaEntry? cached = await OptimizedMediaCache.TryGetAsync(sourcePath);
 
                if (cached is { } entry)
                {
                    ClipTransform transform =
                        GraphSearchHelpers.FindDownstreamTransform(clip.Graph, media)?.Transform ?? new ClipTransform();
 
                    (int requiredWidth, int requiredHeight) = TransformExpressions.ComputeContentSize(
                        transform, nativeWidth, nativeHeight, canvasWidth, canvasHeight);
 
                    if (entry.Width >= requiredWidth && entry.Height >= requiredHeight)
                    {
                        EditSharpConfig.Logger.LogVerbose(
                            $"Using cached optimized media for '{sourcePath}' -> {entry.Path} " +
                            $"({entry.Width}x{entry.Height}, {entry.Codec}).");
 
                        return entry.Path;
                    }
 
                    EditSharpConfig.Logger.LogVerbose(
                        $"Cached optimized media for '{sourcePath}' is {entry.Width}x{entry.Height}, " +
                        $"smaller than this clip needs ({requiredWidth}x{requiredHeight}) — decoding the " +
                        "original source instead.");
                }
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogWarning(
                    $"OptimizedMediaCache lookup failed for '{sourcePath}', decoding the " +
                    $"original source instead: {ex.Message}");
            }
 
            return null;
        }
 
        /// <summary>
        /// True when every Video-type MediaSourceNode across the whole
        /// timeline currently has a persistent OptimizedMediaCache entry with
        /// enough resolution for its own on-screen size — what
        /// Playback.SupportsScrubbing is computed from. A timeline with no
        /// video media inputs at all is trivially true. OPPORTUNISTIC ONLY —
        /// never triggers a build.
        /// </summary>
        public static async Task<bool> AllVideoSourcesHaveSufficientCachedMediaAsync(
            Timeline timeline, int canvasWidth, int canvasHeight)
        {
            var checks = new List<Task<bool>>();
 
            foreach (Channel channel in timeline.Channels)
            {
                foreach (Clip clip in channel.Clips)
                {
                    if (clip is not VideoClip video) continue;
 
                    foreach (MediaSourceNode media in video.Graph.Nodes.OfType<MediaSourceNode>())
                    {
                        if (media.Source.Type != SourceType.Video) continue;
                        checks.Add(CheckOneAsync(video, media, canvasWidth, canvasHeight));
                    }
                }
            }
 
            if (checks.Count == 0) return true;
 
            bool[] results = await Task.WhenAll(checks);
 
            foreach (bool result in results)
            {
                if (!result) return false;
            }
 
            return true;
 
            static async Task<bool> CheckOneAsync(
                VideoClip video, MediaSourceNode media, int canvasWidth, int canvasHeight)
            {
                (int width, int height) = await MediaProbe.GetDimensionsAsync(media.Source.Path);
 
                string? cachedPath = await TryGetSufficientCachedMediaAsync(
                    video, media, media.Source.Path, width, height, canvasWidth, canvasHeight);
 
                return cachedPath != null;
            }
        }
 
        /// <summary>
        /// The frame index at which each video clip's decoders (there may be
        /// more than one — see SkClipContentSource.ReleaseDecoder, which
        /// releases every Video-type MediaSourceNode's decoder for a clip at
        /// once) can be torn down — the LAST frame that clip is visible on.
        /// Still keyed by Clip, not by node — a clip's decoders all share the
        /// clip's own visible window regardless of how many it has.
        /// </summary>
        public static Dictionary<int, List<Clip>> BuildDecoderReleaseSchedule(Timeline timeline, int fps)
        {
            var schedule = new Dictionary<int, List<Clip>>();
 
            foreach (Channel channel in timeline.Channels)
            {
                foreach (Clip clip in channel.Clips)
                {
                    if (clip is not VideoClip video) continue;
                    if (!video.Graph.Nodes.OfType<MediaSourceNode>().Any(m => m.Source.Type == SourceType.Video)) continue;
 
                    int lastVisibleFrame = Math.Max(0, (int)Math.Ceiling(clip.End.TotalSeconds * fps) - 1);
 
                    if (!schedule.TryGetValue(lastVisibleFrame, out List<Clip>? list))
                        schedule[lastVisibleFrame] = list = [];
 
                    list.Add(clip);
                }
            }
 
            return schedule;
        }
    }
}
 