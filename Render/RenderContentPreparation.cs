using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using EditSharp;
using EditSharp.Components;
using EditSharp.Components.Clips;

namespace EditSharp.Render
{
    /// <summary>
    /// Everything about a clip that's constant across its whole life, and the
    /// decoder-release schedule derived from that. Extracted out of Renderer
    /// (formerly FrameRenderer) so Playback can reuse the exact same prep
    /// logic instead of re-deriving it — a full render and a playback session
    /// both start from "what does every clip need before frame 0 can be
    /// drawn," and that answer must not be allowed to drift between the two
    /// callers the way item 8/11's construction-site bugs showed duplicated
    /// logic tends to.
    ///
    /// OPTIMIZED-MEDIA CACHE WIRING, ADDED HERE (decided in conversation):
    /// this is now also the ONE place that decides, per video clip, whether
    /// its decoder opens against the ORIGINAL source file or against a
    /// persistent OptimizedMediaCache entry for that same content — see
    /// ProbeVideoAsync and decodeSourcePaths below. Doing this here, rather
    /// than in Renderer/Playback separately, is exactly the same
    /// "don't duplicate the same decision in two callers" reasoning this
    /// whole class already exists for.
    /// </summary>
    internal static class RenderContentPreparation
    {
        public static Task PrepareContentAsync(
            Timeline timeline, int canvasWidth, int canvasHeight, HardwareAccelerator hwAccel,
            ConcurrentDictionary<Clip, (int, int)> nativeSizes,
            ConcurrentDictionary<Clip, string> staticImagePaths,
            ConcurrentDictionary<Clip, DecodeHwAccelPlan> decodePlans,
            ConcurrentDictionary<Clip, string> decodeSourcePaths,
            ConcurrentBag<string> tempFiles)
        {
            var tasks = new List<Task>();

            foreach (Channel channel in timeline.Channels)
            {
                foreach (Clip clip in channel.Clips.Values)
                {
                    switch (clip)
                    {
                        case SourceClip { Source.Type: SourceType.Video } video:
                            tasks.Add(ProbeVideoAsync(
                                clip, video, canvasWidth, canvasHeight, hwAccel,
                                nativeSizes, decodePlans, decodeSourcePaths));
                            break;

                        case SourceClip { Source.Type: SourceType.Image } image:
                            tasks.Add(PrepareImageAsync(clip, image, nativeSizes, staticImagePaths));
                            break;

                        case TextClip text:
                            PrepareText(clip, text, canvasWidth, canvasHeight,
                                nativeSizes, staticImagePaths, tempFiles);
                            break;
                    }
                }
            }

            return Task.WhenAll(tasks);
        }

        /// <summary>
        /// Probes a video clip's ORIGINAL source for its native size (needed
        /// for aspect-fit math regardless of which file ends up actually
        /// decoded — see below), then decides which file
        /// SkClipContentSource.GetOrOpenDecoder should actually open:
        ///
        ///   - decodeSourcePaths[clip] — the file path to decode. Defaults
        ///     to the clip's own Source.Path; overridden to an
        ///     OptimizedMediaCache entry's Path when one exists AND has
        ///     enough resolution for this clip's own largest on-screen size
        ///     (see the sufficiency check below). nativeSizes[clip] is
        ///     UNCHANGED either way — it always reflects the true original
        ///     source's dimensions, since aspect-fit/placement math must
        ///     stay correct regardless of which file is actually decoded,
        ///     and the cache always preserves the original's aspect ratio
        ///     by construction (see OptimizedMediaCache.ComputeTargetSize).
        ///
        ///   - decodePlans[clip] — DecodeHwAccelPlan.Software, unconditionally,
        ///     when decoding from the cache (DNxHR/ProRes have no hardware
        ///     decode path on any vendor — see OptimizedMediaCache's class
        ///     remarks), or the normal probed hwaccel plan against the
        ///     ORIGINAL source otherwise, exactly as before this cache
        ///     existed.
        ///
        /// OPPORTUNISTIC ONLY, NEVER TRIGGERS A BUILD: this calls
        /// OptimizedMediaCache.TryGetAsync, never GetOrBuildAsync/
        /// PrewarmAsync. Building competes for CPU/disk with the very
        /// playback (or render) this call is trying to serve, which would
        /// work against "keep things smooth" rather than for it. A consumer
        /// app that wants a source's optimized media ready ahead of time —
        /// e.g. right after import — calls OptimizedMediaCache.PrewarmAsync
        /// itself; see that method's own remarks.
        ///
        /// A cache lookup failure (hashing I/O error, permissions, a
        /// genuinely unreadable source) is caught and logged rather than
        /// propagated — it must never take playback/render down on its own,
        /// since decoding the original source directly (this method's
        /// fallback in every failure case) is exactly what already
        /// happened, unconditionally, before this cache existed at all.
        /// </summary>
        private static async Task ProbeVideoAsync(
            Clip clip, SourceClip video, int canvasWidth, int canvasHeight, HardwareAccelerator hwAccel,
            ConcurrentDictionary<Clip, (int, int)> nativeSizes,
            ConcurrentDictionary<Clip, DecodeHwAccelPlan> decodePlans,
            ConcurrentDictionary<Clip, string> decodeSourcePaths)
        {
            (int width, int height) = await MediaProbe.GetDimensionsAsync(video.Source.Path);
            nativeSizes[clip] = (width, height);

            string decodeSourcePath = video.Source.Path;

            try
            {
                OptimizedMediaEntry? cached = await OptimizedMediaCache.TryGetAsync(video.Source.Path);

                if (cached is { } entry)
                {
                    (int requiredWidth, int requiredHeight) = TransformExpressions.ComputeContentSize(
                        clip, width, height, canvasWidth, canvasHeight);

                    //only redirect when the cached proxy has ENOUGH
                    //resolution for this clip's own largest on-screen size
                    //— a clip zoomed in past the cache's configured cap
                    //still needs the true native file, or it would
                    //visibly upscale from the smaller proxy instead. Both
                    //axes independently, matching how MaxScale/
                    //ComputeContentSize already treat width/height
                    //independently (non-uniform scale is a real, supported
                    //case)
                    if (entry.Width >= requiredWidth && entry.Height >= requiredHeight)
                    {
                        decodeSourcePath = entry.Path;

                        EditSharpConfig.Logger.LogVerbose(
                            $"Using cached optimized media for '{video.Source.Path}' -> {entry.Path} " +
                            $"({entry.Width}x{entry.Height}, {entry.Codec}).");
                    }
                    else
                    {
                        EditSharpConfig.Logger.LogVerbose(
                            $"Cached optimized media for '{video.Source.Path}' is {entry.Width}x{entry.Height}, " +
                            $"smaller than this clip needs ({requiredWidth}x{requiredHeight}) — decoding the " +
                            "original source instead.");
                    }
                }
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogWarning(
                    $"OptimizedMediaCache lookup failed for '{video.Source.Path}', decoding the " +
                    $"original source instead: {ex.Message}");
            }

            decodeSourcePaths[clip] = decodeSourcePath;

            decodePlans[clip] = decodeSourcePath == video.Source.Path
                ? await FfmpegRunner.GetDecodePlanAsync(video.Source.Path, hwAccel)
                : DecodeHwAccelPlan.Software;
        }

        private static async Task PrepareImageAsync(
            Clip clip, SourceClip imageClip,
            ConcurrentDictionary<Clip, (int, int)> nativeSizes,
            ConcurrentDictionary<Clip, string> staticImagePaths)
        {
            (int width, int height) = await MediaProbe.GetDimensionsAsync(imageClip.Source.Path);
            nativeSizes[clip] = (width, height);

            staticImagePaths[clip] = imageClip.Source.Path;
        }

        private static void PrepareText(
            Clip clip, TextClip text, int canvasWidth, int canvasHeight,
            ConcurrentDictionary<Clip, (int, int)> nativeSizes,
            ConcurrentDictionary<Clip, string> staticImagePaths,
            ConcurrentBag<string> tempFiles)
        {
            string path = TextRasterizer.Rasterize(
                text, canvasWidth, canvasHeight, out int width, out int height);

            tempFiles.Add(path);
            nativeSizes[clip] = (width, height);
            staticImagePaths[clip] = path;
        }

        /// <summary>
        /// The frame index at which each video clip's decoder can be torn
        /// down — the LAST frame that clip is visible on. Unchanged from a
        /// full-render's perspective; Playback additionally needs the frame
        /// index at which each clip's decoder can be OPENED at an arbitrary
        /// start position, which this method does not compute — see
        /// Playback.ComputeSeekOffsets for that half.
        /// </summary>
        public static Dictionary<int, List<Clip>> BuildDecoderReleaseSchedule(Timeline timeline, int fps)
        {
            var schedule = new Dictionary<int, List<Clip>>();

            foreach (Channel channel in timeline.Channels)
            {
                foreach (Clip clip in channel.Clips.Values)
                {
                    if (clip is not SourceClip { Source.Type: SourceType.Video }) continue;

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