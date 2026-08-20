using System;
using System.Collections.Generic;
using SkiaSharp;
using EditSharp.Components;
using EditSharp.Components.Clips;

namespace EditSharp.Render
{
    /// <summary>
    /// Item 13: the piece FrameRenderConcurrency's removal (item 11) and
    /// SkSourceDecoder's design (also item 11) were both explicitly building
    /// toward — a place for a video clip's decoder to actually LIVE across
    /// many frames, since FrameStateResolver.Resolve is (deliberately)
    /// fully stateless per frame and has nowhere to hold one.
    ///
    /// One instance per render (or per playback session). Owns:
    ///   - one SkSourceDecoder per active video SourceClip, opened lazily on
    ///     that clip's FIRST visible frame and disposed by the caller via
    ///     ReleaseDecoder once the clip's visible window ends (see
    ///     RenderContentPreparation.BuildDecoderReleaseSchedule — same shape
    ///     as the old OptimizedMediaBuilder deletion schedule, just keyed on
    ///     Clip identity instead of a file path).
    ///   - one cached SKImage per Image SourceClip / TextClip, decoded once
    ///     and reused for every frame it's visible on (mirrors the old
    ///     "static image, read once" path exactly, just via SkiaSharp's own
    ///     decoder instead of a per-frame ffmpeg `-loop 1` input).
    ///
    /// GeneratorClip/NoiseClip need no cached state at all — SkGeneratorClip
    /// .Render/SkNoiseClip.Render are pure functions of (clip, time), so
    /// those cases just call straight through every frame.
    ///
    /// CALLER CONTRACT, load-bearing: GetContent must be called for a given
    /// video clip's FrameClip exactly once per frame it's visible on, in
    /// strictly increasing frame order, matching SkSourceDecoder.NextFrame's
    /// own contract — this class does not itself enforce that, it just
    /// forwards to the decoder.
    ///
    /// PLAYBACK ADDITION (seekOffsets): a full render always opens a video
    /// clip's decoder at exactly that clip's own trim start, because
    /// rendering always begins at timeline t=0 — a clip is never already
    /// "in progress" when its decoder first opens. Playback breaks that
    /// assumption: starting playback at an arbitrary Position can land
    /// inside a clip's visible window, and the decoder for that clip needs
    /// to seek to (clip's own trim start + however far into the clip
    /// Position already is), not just the trim start. seekOffsets carries
    /// that additional per-clip offset, keyed the same way nativeSizes
    /// already is. Defaults to null/empty, which reproduces the original
    /// full-render behaviour exactly — Renderer's own construction of this
    /// class is unchanged.
    ///
    /// OPTIMIZED-MEDIA ADDITION (decodeSourcePaths): which FILE a video
    /// clip's decoder actually opens — the clip's own Source.Path by
    /// default, or a persistent OptimizedMediaCache entry for that same
    /// content when RenderContentPreparation.ProbeVideoAsync found one big
    /// enough. seekOffsets/Source.Start math is UNCHANGED either way — see
    /// GetOrOpenDecoder — because the cache always encodes a source's FULL
    /// duration on the same timebase as the original (see
    /// OptimizedMediaCache.EncodeAsync's own remarks), so "seconds from
    /// file start" means the identical thing against either file.
    /// </summary>
    internal sealed class SkClipContentSource : IDisposable
    {
        private readonly int _fps;
        private readonly IReadOnlyDictionary<Clip, (int Width, int Height)> _nativeSizes;
        private readonly IReadOnlyDictionary<Clip, string> _staticImagePaths;
        private readonly IReadOnlyDictionary<Clip, DecodeHwAccelPlan> _decodePlans;
        private readonly IReadOnlyDictionary<Clip, TimeSpan> _seekOffsets;
        private readonly IReadOnlyDictionary<Clip, string> _decodeSourcePaths;

        private readonly Dictionary<Clip, SkSourceDecoder> _videoDecoders = new();
        private readonly Dictionary<Clip, SKImage> _staticContent = new();

        public SkClipContentSource(
            int fps,
            IReadOnlyDictionary<Clip, (int Width, int Height)> nativeSizes,
            IReadOnlyDictionary<Clip, string> staticImagePaths,
            IReadOnlyDictionary<Clip, DecodeHwAccelPlan> decodePlans,
            IReadOnlyDictionary<Clip, TimeSpan>? seekOffsets = null,
            IReadOnlyDictionary<Clip, string>? decodeSourcePaths = null)
        {
            _fps = fps;
            _nativeSizes = nativeSizes;
            _staticImagePaths = staticImagePaths;
            _decodePlans = decodePlans;
            _seekOffsets = seekOffsets ?? new Dictionary<Clip, TimeSpan>();
            _decodeSourcePaths = decodeSourcePaths ?? new Dictionary<Clip, string>();
        }

        /// <summary>
        /// This frame's pixel content for one clip, and whether the CALLER
        /// owns disposing it. Transient content (a video decoder's frame,
        /// a generator's 1x1 fill, a noise field) is fresh every call and
        /// must be disposed by the caller once this frame's draw is done.
        /// Long-lived content (a cached static image) is owned by THIS
        /// class and must NOT be disposed by the caller — it's reused on
        /// every future frame the clip is visible on.
        ///
        /// Returns (null, false) for an audio-only SourceClip — it occupies
        /// a channel slot but draws nothing, same as the old ffmpeg path's
        /// own SourceClip-with-no-video-stream case.
        /// </summary>
        public (SKImage? Image, bool Transient) GetContent(
            FrameClip frameClip, int canvasWidth, int canvasHeight, SkSurfacePool pool)
        {
            switch (frameClip.Clip)
            {
                case SourceClip { Source.Type: SourceType.Video } source:
                    return (GetOrOpenDecoder(frameClip.Clip, source, canvasWidth, canvasHeight).NextFrame(), true);

                case SourceClip { Source.Type: SourceType.Image }:
                case TextClip:
                    return (GetOrLoadStaticImage(frameClip.Clip), false);

                case SourceClip:
                    //audio-only — no video stream to draw
                    return (null, false);

                case GeneratorClip generator:
                    return (SkGeneratorClip.Render(generator, frameClip.ClipSeconds, pool), true);

                case NoiseClip noise:
                    return (SkNoiseClip.Render(noise, frameClip.ClipSeconds, canvasWidth, canvasHeight, pool), true);

                default:
                    throw new NotSupportedException(
                        $"Unknown Clip subtype: {frameClip.Clip.GetType().Name}");
            }
        }

        private SkSourceDecoder GetOrOpenDecoder(Clip clip, SourceClip source, int canvasWidth, int canvasHeight)
        {
            if (_videoDecoders.TryGetValue(clip, out SkSourceDecoder? existing))
                return existing;

            (int nativeWidth, int nativeHeight) = _nativeSizes.TryGetValue(clip, out var size)
                ? size
                : (0, 0);

            if (nativeWidth <= 0 || nativeHeight <= 0)
                throw new InvalidOperationException(
                    "No native size registered for a video clip — MediaProbe must " +
                    "run (see RenderContentPreparation.PrepareContentAsync) before rendering.");

            //Source.Start already carries any head-trim advance (see
            //SourceClip.OnTrimmedFromStart) — this IS the one-time seek
            //SkSourceDecoder's own remarks describe, paid once at stream
            //setup rather than once per frame. seekOffsets adds however far
            //INTO the clip's visible window playback is already starting —
            //zero for a full render (or a playback session starting at
            //Position zero), which reproduces the original behaviour
            //exactly. This math is IDENTICAL whether the file actually
            //opened below is the original source or an OptimizedMediaCache
            //entry — see this class's own remarks on why.
            double startSeconds = (source.Source.Start ?? TimeSpan.Zero).TotalSeconds;

            if (_seekOffsets.TryGetValue(clip, out TimeSpan extra))
                startSeconds += extra.TotalSeconds;

            //Decode target: the SAME max-scale-across-the-clip's-own-keyframe-
            //range content size ClipVideoChain/SkiaClipCompositorSketch.Composite
            //already computes for the Skia resize step (TransformExpressions
            //.ComputeContentSize/.MaxScale — unchanged, reused as-is), capped at
            //native resolution per axis independently. Was previously
            //unconditionally native resolution regardless of how small the clip
            //ever actually renders — flagged as a known redundancy, now closed:
            //ffmpeg no longer decodes/scales detail the compositor was always
            //going to immediately throw away in its own resize step. Computed
            //ONCE per clip at decoder-open time from static keyframe data, not
            //per-frame — still fully compatible with FrameStateResolver.Resolve's
            //stateless-per-frame design, and avoids the ffmpeg-process-restart
            //cost a literal per-frame-varying decode resolution would require.
            //
            //Capped independently per axis (not a single uniform cap) since
            //MaxScale itself is independent per axis (non-uniform scale is a
            //real, supported case) — matches how ComputeContentSize already
            //treats width/height as independent. NOTE: this is computed from
            //the ORIGINAL source's nativeWidth/nativeHeight regardless of
            //which file is actually decoded (see decodeSourcePath below) —
            //correct because an OptimizedMediaCache entry always preserves
            //the original's exact aspect ratio, and RenderContentPreparation.
            //ProbeVideoAsync already verified the cache entry is at least
            //this big before ever redirecting to it.
            (int desiredWidth, int desiredHeight) = TransformExpressions.ComputeContentSize(
                clip, nativeWidth, nativeHeight, canvasWidth, canvasHeight);

            int decodeWidth = Math.Min(desiredWidth, nativeWidth);
            int decodeHeight = Math.Min(desiredHeight, nativeHeight);

            DecodeHwAccelPlan plan = _decodePlans.TryGetValue(clip, out DecodeHwAccelPlan? resolvedPlan)
                ? resolvedPlan
                : DecodeHwAccelPlan.Software;

            //defaults to the clip's own original source path when
            //RenderContentPreparation found no suitable cache entry (or
            //wasn't given the chance to look, e.g. a caller constructing
            //this class directly without going through PrepareContentAsync)
            //— reproduces the pre-cache behaviour exactly in that case
            string decodeSourcePath = _decodeSourcePaths.TryGetValue(clip, out string? overridden)
                ? overridden
                : source.Source.Path;

            SkSourceDecoder decoder = SkSourceDecoder.Start(
                decodeSourcePath, startSeconds, _fps, decodeWidth, decodeHeight, plan);

            _videoDecoders[clip] = decoder;
            return decoder;
        }

        private SKImage GetOrLoadStaticImage(Clip clip)
        {
            if (_staticContent.TryGetValue(clip, out SKImage? cached))
                return cached;

            if (!_staticImagePaths.TryGetValue(clip, out string? path))
                throw new InvalidOperationException(
                    $"No static image path registered for a {clip.GetType().Name} — " +
                    "RenderContentPreparation.PrepareContentAsync must run before rendering.");

            using SKData data = SKData.Create(path)
                ?? throw new InvalidOperationException($"Could not read '{path}'.");

            SKImage image = SKImage.FromEncodedData(data)
                ?? throw new InvalidOperationException($"Could not decode image '{path}'.");

            _staticContent[clip] = image;
            return image;
        }

        /// <summary>
        /// Terminates and forgets a clip's video decoder once its visible
        /// window is over (see RenderContentPreparation's decoder-release
        /// schedule). A no-op for any clip that never had one — safe to
        /// call unconditionally rather than requiring the caller to know
        /// which clips are video.
        /// </summary>
        public void ReleaseDecoder(Clip clip)
        {
            if (_videoDecoders.Remove(clip, out SkSourceDecoder? decoder))
                decoder.Dispose();
        }

        public void Dispose()
        {
            foreach (SkSourceDecoder decoder in _videoDecoders.Values) decoder.Dispose();
            _videoDecoders.Clear();

            foreach (SKImage image in _staticContent.Values) image.Dispose();
            _staticContent.Clear();
        }
    }
}