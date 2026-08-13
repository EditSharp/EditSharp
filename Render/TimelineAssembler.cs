using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EditSharp.Components;
using EditSharp.Components.Clips;
using EditSharp.Assembly;

namespace EditSharp.Render
{
    /// <summary>
    /// Renders a <see cref="Blueprint"/>'s timeline to a single output file.
    ///
    /// This is the entry point and orchestrator only; each stage lives in its own
    /// class under the Assembly namespace, so a bug in one of them can be
    /// investigated without loading the whole pipeline into view:
    ///
    ///   ClipContentBuilder   source/text/generator -> raw content stream
    ///   ClipVideoChain       framing, effects, Modulate, transform
    ///   ClipEffects          the individual effects, both stages
    ///   TransformExpressions ClipTransform + keyframes -> perspective corners
    ///   ChannelCompositor    segments, transitions, blend modes
    ///   TimelineCompositor   channel stacking, flatten to opaque
    ///   AudioMixer           per-clip placement, channel volume, mixdown
    ///   FfmpegRunner         encoder selection and the actual process
    ///
    /// ASSUMPTIONS
    ///   - Blueprint.Resolution is (Width, Height).
    ///   - Blueprint.OutputDirectory is the full output FILE path including
    ///     extension; ffmpeg infers the container from it.
    ///   - ffmpeg/ffprobe are resolved via EditSharpConfig.FfmpegPath and
    ///     EditSharpConfig.FfprobePath, both of which default to resolving
    ///     from PATH ("ffmpeg"/"ffprobe") unless the host app overrides them.
    ///   - GIF output carries no audio.
    /// </summary>
    public class TimelineAssembler
    {
        public async Task AssembleAsync(Blueprint blueprint)
        {
            var watch = Stopwatch.StartNew();

            Validate(blueprint);

            int width = blueprint.Resolution.Item1;
            int height = blueprint.Resolution.Item2;
            int fps = blueprint.Framerate;

            var graph = new InputGraph();
            var tempFiles = new ConcurrentBag<string>();

            try
            {
                // Every clip's raw content is built ONCE and shared between the
                // video and audio paths. Building per-path instead would register
                // each source as a separate ffmpeg input and decode it twice.
                Dictionary<Clip, ClipContent> contents = await BuildContentAsync(
                    blueprint.Timeline, graph, width, height, fps, tempFiles);

                string video = TimelineCompositor.Compose(
                    blueprint.Timeline, contents, graph, width, height, fps, tempFiles);

                string audio = AudioMixer.Compose(blueprint.Timeline, contents, graph);

                await FfmpegRunner.RunFfmpegAsync(graph, video, audio, blueprint, fps);
            }
            finally
            {
                foreach (string path in tempFiles)
                {
                    try { File.Delete(path); } catch { /* best-effort cleanup */ }
                }

                watch.Stop();

                string info = $"{width}x{height}@{fps}fps";
                string message =
                    $"{info} timeline render completed in " +
                    $"{watch.Elapsed.TotalSeconds:F2} seconds\n{blueprint.OutputDirectory}";

                EditSharpConfig.Logger.Log(message);
            }
        }

        /// <summary>
        /// Walks every channel and resolves each clip to its content stream.
        ///
        /// Sequential rather than parallel: InputGraph.AddInput hands out indices
        /// that must match ffmpeg's own -i ordering, and although it is lock-guarded,
        /// running clips concurrently would make that ordering depend on which
        /// probe finished first. The work here is mostly ffprobe calls and a little
        /// rasterization, so the ordering guarantee is worth more than the overlap.
        /// </summary>
        private static async Task<Dictionary<Clip, ClipContent>> BuildContentAsync(
            Timeline timeline, InputGraph graph,
            int width, int height, int fps, ConcurrentBag<string> tempFiles)
        {
            var contents = new Dictionary<Clip, ClipContent>();

            foreach (Channel channel in timeline.Channels)
            {
                foreach (Clip clip in channel.Clips.Values.OrderBy(c => c.Start))
                {
                    if (contents.ContainsKey(clip)) continue;

                    contents[clip] = await ClipContentBuilder.BuildAsync(
                        clip, graph, width, height, fps, tempFiles);
                }
            }

            return contents;
        }

        private static void Validate(Blueprint blueprint)
        {
            if (blueprint.Timeline == null || blueprint.Timeline.Channels.Count == 0)
                throw new ArgumentException("Blueprint.Timeline must contain at least one Channel.");

            if (blueprint.Timeline.Channels.All(c => c.Clips.Count == 0))
                throw new ArgumentException("Blueprint.Timeline contains no clips on any channel.");

            if (blueprint.Resolution.Item1 <= 0 || blueprint.Resolution.Item2 <= 0)
                throw new ArgumentException("Blueprint.Resolution must have positive width and height.");

            if (blueprint.Framerate <= 0)
                throw new ArgumentException("Blueprint.Framerate must be positive.");

            if (string.IsNullOrWhiteSpace(blueprint.OutputDirectory))
                throw new ArgumentException("Blueprint.OutputDirectory must be a full output file path.");

            // A transition that does not preserve alpha punches an opaque rectangle
            // through everything beneath it for its whole duration. Harmless on the
            // bottom channel, which is flattened onto black anyway.
            foreach (Channel channel in blueprint.Timeline.Channels.Skip(1))
            {
                foreach ((Clip clip, Transition transition) in channel.Transitions)
                {
                    if (Constants.PreservesAlpha(transition.Type)) continue;

                    throw new ArgumentException(
                        $"Channel '{channel.Name}' uses transition {transition.Type}, which does not " +
                        $"preserve transparency, so it would black out the channels beneath it for " +
                        $"the length of the transition. Use one of the alpha-safe transitions, or " +
                        $"move this channel to the bottom of the timeline.");
                }
            }
        }
    }
}
