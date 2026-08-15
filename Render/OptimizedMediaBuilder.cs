using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EditSharp;
using EditSharp.Components;
using EditSharp.Components.Clips;
using EditSharp.Components.Effects;

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
        /// One clip's optimized media: its temp file path, the output frame
        /// index after which it is safe to delete, and the file's own actual
        /// dimensions/duration — probed once here so nothing downstream needs a
        /// second ffprobe call just to answer "how big is this" or "how much
        /// real content is in it".
        ///
        /// AvailableSeconds is what FrameStateResolver clamps a seek to: a clip
        /// whose Duration outlasts its source freezes on the last real frame
        /// (this pipeline's current extension behaviour) rather than looping or
        /// running past the end of the file.
        ///
        /// DeleteAfterFrame is INCLUSIVE — the file may still be needed BY that
        /// frame, so only delete once rendering has moved past it.
        ///
        /// EffectsBaked is true when this clip's EffectStage.PreTransform
        /// effects were already applied once here, over the whole clip, rather
        /// than left for the per-frame path to redo on every frame the clip is
        /// visible on (see VideoUtils.ReencodeVideoAsync's preTransformEffects
        /// parameter). ClipVideoChain.Build must be told about this — see its
        /// preTransformEffectsBaked parameter — or the effects apply twice.
        /// PostTransform effects (DropShadowEffect by default) are NEVER baked
        /// here regardless of this flag: they depend on the clip's time-varying
        /// position within the canvas, which doesn't exist yet at this stage.
        /// </summary>
        public readonly record struct OptimizedMedia(
            string Path, int DeleteAfterFrame,
            int NativeWidth, int NativeHeight, double AvailableSeconds,
            bool EffectsBaked);

        /// <summary>
        /// Builds optimized media for every SourceClip backed by a Video
        /// source, across every channel. TextClip/GeneratorClip/NoiseClip
        /// never decode through ffmpeg here — nothing to optimize — and are
        /// skipped, as is a SourceClip whose Source is an image or audio-only
        /// file.
        ///
        /// canvasWidth/canvasHeight decide both how large optimized media is
        /// built (see ComputeOptimizedMediaSize) and how any baked PreTransform
        /// effects are normalized — both need to match the actual render, not
        /// an arbitrary size, so they're required rather than optional.
        ///
        /// tempFiles should be the render's own bag (the one FrameRenderer
        /// sweeps at the end) — baking a clip's effects can create a mask file
        /// (see ClipEffects.MaskCache) that isn't part of the OptimizedMedia
        /// this method returns, so it needs its own path into cleanup.
        ///
        /// maxConcurrency should come from Blueprint.ExtractionConcurrency —
        /// see its remarks for why this defaults conservatively rather than
        /// to processor count.
        /// </summary>
        public static async Task<Dictionary<Clip, OptimizedMedia>> BuildAsync(
            Timeline timeline, int fps, int canvasWidth, int canvasHeight,
            ConcurrentBag<string> tempFiles, int maxConcurrency)
        {
            var results = new ConcurrentDictionary<Clip, OptimizedMedia>();
            using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));

            var tasks = new List<Task>();

            foreach (Channel channel in timeline.Channels)
            {
                foreach (Clip clip in channel.Clips.Values)
                {
                    //already queued via another channel? a Clip only ever
                    //belongs to one channel, so this can't happen today, but
                    //guarding costs nothing and saves a wasted re-encode if
                    //that ever changes
                    if (results.ContainsKey(clip)) continue;

                    //`perlin` is a GENERATOR SOURCE with no seek — it produces
                    //frames strictly sequentially from frame 0, so asking it
                    //for output frame N (via trim=start_frame=N) makes ffmpeg
                    //generate and discard every frame before it. Per-frame
                    //that's O(N) work for frame N, making the whole render
                    //O(N^2): measured at a dead-linear +28ms per frame index
                    //on a 1080p noise clip, which is almost exactly the cost
                    //of generating one 1080p perlin frame. Pre-rendering the
                    //whole noise stream ONCE into intra-only FFV1 turns every
                    //per-frame read back into an ordinary frame-exact seek —
                    //the identical problem, and identical fix, that optimized
                    //media already solves for video sources.
                    if (clip is NoiseClip noiseClip)
                    {
                        tasks.Add(BuildNoiseAsync(
                            noiseClip, fps, canvasWidth, canvasHeight, gate, results));
                        continue;
                    }

                    if (clip is not SourceClip sourceClip) continue;
                    if (sourceClip.Source.Type != SourceType.Video) continue;

                    tasks.Add(BuildOneAsync(
                        sourceClip, clip, fps, canvasWidth, canvasHeight,
                        tempFiles, gate, results));
                }
            }

            EditSharpConfig.Logger.Log(
                $"Building optimized media for {tasks.Count} clip(s) (concurrency {maxConcurrency})...");
            var sw = Stopwatch.StartNew();

            await Task.WhenAll(tasks);

            EditSharpConfig.Logger.Log(
                $"Optimized media built for {tasks.Count} clip(s) in {sw.ElapsedMilliseconds}ms.");

            return new Dictionary<Clip, OptimizedMedia>(results);
        }

        /// <summary>
        /// Pre-renders a NoiseClip's entire `perlin` stream to intra-only FFV1,
        /// so the per-frame render can seek into it instead of regenerating
        /// every preceding frame (see the O(N^2) explanation at the call site).
        ///
        /// Rendered at CANVAS size, not scaled by the clip's keyframe scale the
        /// way a video source's optimized media is: `perlin` is generated at
        /// whatever resolution it's asked for, and its Detail/SeetheRate are
        /// already resolution-independent by construction (xscale is "noise
        /// cells across the frame", not a pixel count), so generating larger
        /// than canvas buys no extra detail — it would just be a bigger
        /// version of the same pattern. There is also no native resolution to
        /// avoid upscaling past, since nothing is being resampled from a
        /// source file.
        ///
        /// No effects are baked here. A NoiseClip's PreTransform effects are
        /// still applied per-frame by ClipVideoChain, unlike a video source's
        /// — this method exists purely to make the noise itself seekable, and
        /// keeping the two concerns separate avoids duplicating
        /// OptimizedMediaEffectsBaker's whole graph-building path for a case
        /// that hasn't been shown to need it.
        /// </summary>
        private static async Task BuildNoiseAsync(
            NoiseClip clip, int fps, int canvasWidth, int canvasHeight,
            SemaphoreSlim gate, ConcurrentDictionary<Clip, OptimizedMedia> results)
        {
            await gate.WaitAsync();
            try
            {
                EditSharpConfig.Logger.LogVerbose(
                    $"Noise optimized media starting ({clip.Duration.TotalSeconds:F2}s @ " +
                    $"{canvasWidth}x{canvasHeight})...");

                var sw = Stopwatch.StartNew();

                string path = await NoiseRenderer.RenderAsync(
                    clip, fps, canvasWidth, canvasHeight);

                MediaInfo info = await MediaProbe.ProbeAsync(path);
                double available = info.Duration?.TotalSeconds ?? clip.Duration.TotalSeconds;

                //same half-open [Start, End) reasoning as BuildOneAsync's
                int deleteAfterFrame = (int)Math.Ceiling(clip.End.TotalSeconds * fps) - 1;

                results[clip] = new OptimizedMedia(
                    path, deleteAfterFrame, info.Width, info.Height, available,
                    EffectsBaked: false);

                EditSharpConfig.Logger.LogVerbose(
                    $"Noise optimized media built in {sw.ElapsedMilliseconds}ms " +
                    $"({info.Width}x{info.Height}, {available:F2}s available) -> {path}");
            }
            finally
            {
                gate.Release();
            }
        }

        private static async Task BuildOneAsync(
            SourceClip sourceClip, Clip clip, int fps, int canvasWidth, int canvasHeight,
            ConcurrentBag<string> tempFiles,
            SemaphoreSlim gate, ConcurrentDictionary<Clip, OptimizedMedia> results)
        {
            await gate.WaitAsync();
            try
            {
                //logged the moment this clip actually gets a concurrency slot
                //and starts work — if a caller reports a hang with no logs,
                //this line (or its absence) is what tells you whether it's
                //stuck queued behind ExtractionConcurrency, or actually
                //inside the probe/reencode stages below
                EditSharpConfig.Logger.LogVerbose(
                    $"Optimized media starting for '{sourceClip.Source.Path}'...");

                var stageSw = Stopwatch.StartNew();

                //native size of the SOURCE (not the eventual optimized-media
                //output) — needed up front to decide how much, if any,
                //downscale to apply
                MediaInfo sourceInfo = await MediaProbe.ProbeAsync(sourceClip.Source.Path);
                long sourceProbeMs = stageSw.ElapsedMilliseconds;
                stageSw.Restart();

                (int Width, int Height)? scaleTo = ComputeOptimizedMediaSize(
                    sourceInfo.Width, sourceInfo.Height, canvasWidth, canvasHeight,
                    MaxKeyframeScale(clip));

                //only EffectStage.PreTransform effects are eligible for baking
                //— a PostTransform effect (DropShadowEffect by default) needs
                //the clip's time-varying position within the canvas, which
                //doesn't exist yet at this stage. See ReencodeVideoAsync's own
                //remarks for the reasoning this mirrors
                List<Effect> bakeable = clip.Effects
                    .Where(e => e.Enabled && e.Stage == EffectStage.PreTransform)
                    .ToList();

                string path = bakeable.Count > 0
                    ? await OptimizedMediaEffectsBaker.BakeAsync(
                        sourceClip.Source, VideoCodec.FFV1, scaleTo, bakeable,
                        canvasWidth, canvasHeight, fps, tempFiles)
                    : await VideoUtils.ReencodeVideoAsync(sourceClip.Source, VideoCodec.FFV1, scaleTo);
                long reencodeMs = stageSw.ElapsedMilliseconds;
                stageSw.Restart();

                //probing the OPTIMIZED file rather than re-deriving from
                //Source/SourceTiming a second time — the file on disk is the
                //single source of truth for what actually got built, whatever
                //trimming/clamping/scaling produced it
                MediaInfo info = await MediaProbe.ProbeAsync(path);
                long outputProbeMs = stageSw.ElapsedMilliseconds;
                double available = info.Duration?.TotalSeconds ?? clip.Duration.TotalSeconds;

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

                results[clip] = new OptimizedMedia(
                    path, deleteAfterFrame, info.Width, info.Height, available,
                    EffectsBaked: bakeable.Count > 0);

                EditSharpConfig.Logger.LogVerbose(
                    $"Optimized media for '{sourceClip.Source.Path}' done: source probe {sourceProbeMs}ms, " +
                    $"reencode {reencodeMs}ms, output probe {outputProbeMs}ms " +
                    $"({sourceInfo.Width}x{sourceInfo.Height} -> {info.Width}x{info.Height}, " +
                    $"{available:F2}s available, {bakeable.Count} effect(s) baked) -> {path}");
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// The resolution to build a clip's optimized media at: a box of
        /// canvasSize * the clip's largest keyframed Scale (see
        /// MaxKeyframeScale — this can be smaller OR larger than canvas
        /// resolution, in either direction, since it tracks the clip's own
        /// largest actual on-screen size rather than assuming canvas is a
        /// floor), with the source's own aspect ratio preserved inside it —
        /// never distorted to fit the box exactly — and never upscaled past
        /// the source's own native resolution.
        ///
        /// This is the fix for optimized media being built at full native
        /// resolution regardless of how the clip is ever actually displayed:
        /// a clip shown at canvas size or smaller was still having every
        /// frame decoded at, say, source 4K when the render only ever needed
        /// 1080p — 4x the pixels, on every one of hundreds of per-frame
        /// decodes, for detail the composite never uses.
        ///
        /// Returns null when the source is already at or below the box —
        /// re-encoding at the same or a larger size than the source has
        /// nothing to gain and would either waste work or fabricate detail
        /// that was never there.
        /// </summary>
        private static (int Width, int Height)? ComputeOptimizedMediaSize(
            int nativeWidth, int nativeHeight, int canvasWidth, int canvasHeight, float maxScale)
        {
            if (nativeWidth <= 0 || nativeHeight <= 0) return null;

            double boxWidth = canvasWidth * maxScale;
            double boxHeight = canvasHeight * maxScale;

            double fit = Math.Min(boxWidth / nativeWidth, boxHeight / nativeHeight);

            if (fit >= 1.0) return null; //already small enough — keep native quality

            return ((int)Math.Round(nativeWidth * fit), (int)Math.Round(nativeHeight * fit));
        }

        /// <summary>
        /// The largest Scale this clip ever reaches across its base Transform
        /// and every keyframe — mirrors TextRasterizer.ScaleCeiling's
        /// reasoning (build source material sized for the biggest zoom the
        /// clip ever performs, not the opening frame) but with NO upper cap:
        /// TextRasterizer caps at MaxRasterScale because rasterizing
        /// arbitrarily large text is nearly free, where optimized media is a
        /// real lossless re-encode with a real decode cost per pixel on every
        /// frame it's read back — ComputeOptimizedMediaSize is what decides
        /// whether the resulting resolution is worth it, not this method.
        ///
        /// NO lower floor either — a clip that's only ever shown at 0.6x
        /// canvas scale gets optimized media sized for 0.6x canvas, smaller
        /// than the render itself, for the same reason: nothing downstream
        /// ever samples more detail than the clip's own largest on-screen
        /// size actually shows, so building bigger than that buys nothing.
        /// Clamped to a small positive epsilon rather than allowing exactly 0
        /// or negative, which would ask ffmpeg to scale to a degenerate
        /// zero-size buffer.
        /// </summary>
        private static float MaxKeyframeScale(Clip clip)
        {
            float largest = Math.Max(clip.Transform.Scale.X, clip.Transform.Scale.Y);

            foreach (Keyframe keyframe in clip.Keyframes)
            {
                largest = Math.Max(largest,
                    Math.Max(keyframe.Transform.Scale.X, keyframe.Transform.Scale.Y));
            }

            return Math.Max(largest, 0.01f);
        }
    }
}
