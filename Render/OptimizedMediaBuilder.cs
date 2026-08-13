using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EditSharp.Components;
using EditSharp.Components.Clips;

namespace EditSharp.Render
{
    /// <summary>
    /// Builds optimized media (see VideoUtils.ReencodeVideoAsync) for every
    /// video source clip in a timeline, ahead of a frame-by-frame render.
    ///
    /// Every clip is queued and built UPFRONT, not on a rolling window keyed
    /// to render progress. That trades RAM/decoder-footprint pressure for
    /// disk space — which avoids the multi-decoder-single-process allocation
    /// pattern that caused this project's earlier OOM crashes — and it means
    /// no runtime reference-counting is needed to decide when a clip's
    /// optimized media is safe to delete: every clip's Start/Duration/End is
    /// already known before the render starts, so DeleteAfterFrame is fully
    /// precomputable rather than tracked live.
    ///
    /// Clips that share the same underlying Source.Path are NOT deduplicated
    /// — each Clip gets its own optimized-media build, matching how
    /// ClipContentBuilder already works per-Clip today. Worth revisiting as
    /// an optimization later; not a correctness issue now.
    /// </summary>
    internal static class OptimizedMediaBuilder
    {
        /// <summary>
        /// One clip's optimized media: its temp file path, and the output
        /// frame index after which it is safe to delete. DeleteAfterFrame is
        /// INCLUSIVE — the file may still be needed BY that frame, so only
        /// delete once rendering has moved past it.
        /// </summary>
        public readonly record struct OptimizedMedia(string Path, int DeleteAfterFrame);

        /// <summary>
        /// Builds optimized media for every SourceClip backed by a Video
        /// source, across every channel. TextClip/GeneratorClip/NoiseClip
        /// never decode through ffmpeg here — nothing to optimize — and are
        /// skipped, as is a SourceClip whose Source is an image or audio-only
        /// file.
        ///
        /// maxConcurrency should come from Blueprint.ExtractionConcurrency —
        /// see its remarks for why this defaults conservatively rather than
        /// to processor count.
        /// </summary>
        public static async Task<Dictionary<Clip, OptimizedMedia>> BuildAsync(
            Timeline timeline, int fps, int maxConcurrency)
        {
            var results = new ConcurrentDictionary<Clip, OptimizedMedia>();
            using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));

            var tasks = new List<Task>();

            foreach (Channel channel in timeline.Channels)
            {
                foreach (Clip clip in channel.Clips.Values)
                {
                    if (clip is not SourceClip sourceClip) continue;
                    if (sourceClip.Source.Type != SourceType.Video) continue;

                    //already queued via another channel? a Clip only ever
                    //belongs to one channel, so this can't happen today, but
                    //guarding costs nothing and saves a wasted re-encode if
                    //that ever changes
                    if (results.ContainsKey(clip)) continue;

                    tasks.Add(BuildOneAsync(sourceClip, clip, fps, gate, results));
                }
            }

            await Task.WhenAll(tasks);

            return new Dictionary<Clip, OptimizedMedia>(results);
        }

        private static async Task BuildOneAsync(
            SourceClip sourceClip, Clip clip, int fps,
            SemaphoreSlim gate, ConcurrentDictionary<Clip, OptimizedMedia> results)
        {
            await gate.WaitAsync();
            try
            {
                string path = await VideoUtils.ReencodeVideoAsync(sourceClip.Source, VideoCodec.FFV1);

                //Clips are half-open [Start, End) elsewhere in this codebase
                //(see Channel.InsertClip's "intersection is exactly zero" for
                //touching clips), so the last output frame this clip can still
                //be needed for is the largest N with N/fps < End — NOT simply
                //floor(End*fps), which over-includes by one whenever End*fps
                //lands exactly on an integer (e.g. End=2.5s at 10fps: frame 25
                //sits AT End, not before it, so 24 is the true last frame, but
                //Floor(25.0) wrongly gives 25). Ceiling(x)-1 gives the same
                //answer as Floor(x) everywhere x isn't an exact integer, and
                //is correct at the boundary where Floor(x) is not
                int deleteAfterFrame = (int)Math.Ceiling(clip.End.TotalSeconds * fps) - 1;

                results[clip] = new OptimizedMedia(path, deleteAfterFrame);
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
