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
    /// No behaviour change from the pre-extraction version in Renderer.cs —
    /// this is a pure move, not a rewrite.
    /// </summary>
    internal static class RenderContentPreparation
    {
        public static Task PrepareContentAsync(
            Timeline timeline, int canvasWidth, int canvasHeight, HardwareAccelerator hwAccel,
            ConcurrentDictionary<Clip, (int, int)> nativeSizes,
            ConcurrentDictionary<Clip, string> staticImagePaths,
            ConcurrentDictionary<Clip, DecodeHwAccelPlan> decodePlans,
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
                            tasks.Add(ProbeVideoAsync(clip, video, hwAccel, nativeSizes, decodePlans));
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

        private static async Task ProbeVideoAsync(
            Clip clip, SourceClip video, HardwareAccelerator hwAccel,
            ConcurrentDictionary<Clip, (int, int)> nativeSizes,
            ConcurrentDictionary<Clip, DecodeHwAccelPlan> decodePlans)
        {
            (int width, int height) = await MediaProbe.GetDimensionsAsync(video.Source.Path);
            nativeSizes[clip] = (width, height);

            decodePlans[clip] = await FfmpegRunner.GetDecodePlanAsync(video.Source.Path, hwAccel);
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
        /// PlaybackContentPreparation.ComputeSeekOffsets for that half.
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
