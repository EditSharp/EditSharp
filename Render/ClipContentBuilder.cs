using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using SkiaSharp;
using EditSharp.Components;
using EditSharp.Components.Clips;

namespace EditSharp.Render
{
    /// <summary>
    /// What one clip contributes before any framing, effects or transform happen.
    ///
    /// VideoLabel is null for clips that are audio only — they occupy their slot on
    /// the channel without drawing anything, so nothing beneath them is occluded.
    /// AudioLabel is null for clips that make no sound.
    /// </summary>
    internal readonly record struct ClipContent(
        string? VideoLabel,
        int NativeWidth,
        int NativeHeight,
        string? AudioLabel,
        bool ModulateAlreadyApplied);

    /// <summary>
    /// Turns a Clip into its raw content stream, at the source's own resolution and
    /// already trimmed (or looped, or held) to exactly the clip's timeline
    /// duration. Everything downstream — framing, effects, transform — assumes the
    /// stream is already the right length, so all the duration reconciliation
    /// happens here.
    /// </summary>
    internal static class ClipContentBuilder
    {
        /// <summary>
        /// audioOnly skips building a video filter chain entirely — not just
        /// leaving VideoLabel null on the returned ClipContent, but never
        /// emitting the filter lines that would have produced it. This is
        /// not an optimization applied on top of otherwise-normal behaviour:
        /// an unconsumed filter output is a HARD bind-time error in ffmpeg
        /// ("Filter 'fps:default' has output 0 unconnected"), confirmed
        /// directly. FinalizeOutputAsync is this method's only caller since
        /// the whole-window pipeline was removed, and it only ever wants
        /// audio, so audioOnly:true is what it passes — every clip that
        /// contributes NOTHING to audio (TextClip, GeneratorClip, NoiseClip,
        /// an Image source) skips its filter chain outright rather than
        /// building one nothing will ever map.
        /// </summary>
        public static async Task<ClipContent> BuildAsync(
            Clip clip, InputGraph graph,
            int canvasWidth, int canvasHeight, int fps,
            ConcurrentBag<string> tempFiles, bool audioOnly = false)
        {
            return clip switch
            {
                SourceClip source => await BuildSourceClipAsync(
                    source, graph, canvasWidth, canvasHeight, fps, audioOnly),

                //none of these three ever produce audio — in audioOnly mode
                //they contribute nothing at all, so nothing is built for them
                TextClip when audioOnly => Empty(canvasWidth, canvasHeight),
                GeneratorClip when audioOnly => Empty(canvasWidth, canvasHeight),
                NoiseClip when audioOnly => Empty(canvasWidth, canvasHeight),

                TextClip text => BuildTextClip(
                    text, graph, canvasWidth, canvasHeight, fps, tempFiles),

                GeneratorClip generator => BuildGeneratorClip(
                    generator, graph, canvasWidth, canvasHeight, fps),

                NoiseClip noise => BuildNoiseClip(
                    noise, graph, canvasWidth, canvasHeight, fps),

                _ => throw new NotSupportedException(
                    $"Unknown Clip subtype: {clip.GetType().Name}"),
            };
        }

        /// <summary>A clip contributing nothing at all — no video, no audio, no filter lines.</summary>
        private static ClipContent Empty(int canvasWidth, int canvasHeight) =>
            new(null, canvasWidth, canvasHeight, null, false);

        private static async Task<ClipContent> BuildSourceClipAsync(
            SourceClip clip, InputGraph graph, int canvasWidth, int canvasHeight, int fps,
            bool audioOnly)
        {
            Source source = clip.Source;
            double clipSeconds = clip.Duration.TotalSeconds;

            //an image has no audio stream, full stop — in audioOnly mode it
            //contributes nothing, so skip it before even probing rather than
            //building a trim/loop/fps chain nothing will ever map
            if (audioOnly && source.Type == SourceType.Image)
                return Empty(canvasWidth, canvasHeight);

            //ONE ffprobe for the whole clip. Dimensions, duration and whether there
            //is an audio stream used to be three separate calls, which meant three
            //processes per clip and most of the wall time before ffmpeg even started
            MediaInfo info = await MediaProbe.ProbeAsync(source.Path);

            bool hasExplicitRange = source.Start.HasValue || source.Duration.HasValue;

            if (source.Type == SourceType.Image)
            {
                //an image has no inherent length, so it simply holds for however
                //long the clip runs — no looping question arises
                if (!info.HasVideo)
                    throw new InvalidOperationException(
                        $"Source '{source.Path}' is typed as an Image but has no image stream.");

                int imageIndex = graph.AddInput(source.Path, true, ["-loop", "1"]);

                string imageLabel = graph.NextLabel("ccimg");
                graph.FilterLines.Add(
                    $"[{imageIndex}:v]trim=duration={GraphUtilities.Num(clipSeconds)}," +
                    $"setpts=PTS-STARTPTS,fps={fps}[{imageLabel}]");

                return new ClipContent(imageLabel, info.Width, info.Height, null, false);
            }

            //SourceTiming owns the Start/Duration defaults and the clamp against
            //what the file actually holds, so the pipeline and any calling code
            //that sized a clip from the same source agree on the length
            var (sourceStart, available) = SourceTiming.ResolveRange(source, info.Duration);

            bool needsLoop = clipSeconds > available.TotalSeconds + 0.001;

            //Whole-file looping is handled at the demuxer, which costs nothing.
            //-stream_loop cannot loop a SUBRANGE though — verified directly: an
            //input trimmed with -ss/-t and looped still yields just the one pass —
            //so an explicit Source range has to go through the `loop` filter, which
            //buffers frames in memory.
            bool canStreamLoop = needsLoop && !hasExplicitRange;

            string[]? extraArgs = canStreamLoop ? ["-stream_loop", "-1"] : null;

            //the only input in the whole pipeline whose decode a GPU could take
            //over. An Audio source has no video stream to decode, and the image
            //path above (plus the rasterized text/mask PNGs) are still images —
            //asking for hardware decode on those fails the run rather than being
            //ignored. -hwaccel is a per-input option and sits happily alongside
            //-stream_loop, verified directly
            int index = graph.AddInput(
                source.Path, true, extraArgs,
                hardwareDecodable: source.Type == SourceType.Video);

            string? videoLabel = null;
            int nativeWidth = canvasWidth;
            int nativeHeight = canvasHeight;

            //the input registration above still has to happen even when
            //audioOnly and this is a Video-type source: the audio track (if
            //any) lives in this SAME file, referenced via this SAME index as
            //[index:a] below. What's skipped is only the VIDEO filter chain
            //itself (BuildVideoStream) — that's the part nothing would ever
            //consume in an audio-only pass
            if (source.Type == SourceType.Video && !audioOnly)
            {
                if (!info.HasVideo)
                    throw new InvalidOperationException(
                        $"Source '{source.Path}' is typed as Video but has no video stream.");

                nativeWidth = info.Width;
                nativeHeight = info.Height;

                videoLabel = BuildVideoStream(
                    graph, index, sourceStart, available, clipSeconds, fps,
                    needsLoop, canStreamLoop, source.Path);
            }
            else if (source.Type == SourceType.Video)
            {
                nativeWidth = info.Width;
                nativeHeight = info.Height;
            }

            string? audioLabel = null;
            //an Audio-typed source with no audio stream is a mistake worth naming,
            //not a clip that silently contributes nothing
            if (source.Type == SourceType.Audio && !info.HasAudio)
                throw new InvalidOperationException(
                    $"Source '{source.Path}' is typed as Audio but has no audio stream.");

            bool hasAudio = info.HasAudio;

            if (hasAudio)
            {
                audioLabel = graph.NextLabel("ccaud");

                //when the demuxer is looping, the stream already runs indefinitely,
                //so a plain duration trim is all that's needed
                string trim = canStreamLoop
                    ? $"atrim=duration={GraphUtilities.Num(clipSeconds)}"
                    : $"atrim=start={GraphUtilities.Sec(sourceStart)}:" +
                      $"end={GraphUtilities.Sec(sourceStart + available)}";

                //video holds its last frame when a subrange runs short, but there is
                //no audible equivalent of a freeze — a repeated sample would buzz —
                //so the tail is padded with silence instead
                string loopStage = needsLoop && !canStreamLoop
                    ? $",apad=whole_dur={GraphUtilities.Num(clipSeconds)}"
                    : "";

                graph.FilterLines.Add(
                    $"[{index}:a]{trim},asetpts=PTS-STARTPTS{loopStage}," +
                    $"volume={GraphUtilities.Num(clip.Volume)}[{audioLabel}]");
            }

            //an audio source contributes no picture at all, so nothing beneath it
            //on the timeline gets covered up
            if (source.Type == SourceType.Audio) videoLabel = null;

            return new ClipContent(videoLabel, nativeWidth, nativeHeight, audioLabel, false);
        }

        private static string BuildVideoStream(
            InputGraph graph, int index,
            TimeSpan sourceStart, TimeSpan available, double clipSeconds, int fps,
            bool needsLoop, bool canStreamLoop, string sourcePath)
        {
            string label = graph.NextLabel("ccvid");

            if (canStreamLoop)
            {
                //demuxer is already repeating the file; just take what's needed
                graph.FilterLines.Add(
                    $"[{index}:v]trim=duration={GraphUtilities.Num(clipSeconds)}," +
                    $"setpts=PTS-STARTPTS,fps={fps}[{label}]");

                return label;
            }

            if (!needsLoop)
            {
                graph.FilterLines.Add(
                    $"[{index}:v]trim=start={GraphUtilities.Sec(sourceStart)}:" +
                    $"end={GraphUtilities.Sec(sourceStart + available)}," +
                    $"setpts=PTS-STARTPTS,fps={fps}[{label}]");

                return label;
            }

            //An explicit Source range that runs short holds its final frame for the
            //remainder instead of repeating.
            //
            //-stream_loop cannot loop a subrange — verified directly, an input
            //trimmed with -ss/-t and looped still yields a single pass — and the
            //`loop` filter can, but only by buffering every frame uncompressed:
            //roughly 8MB per frame at 1080p, so a five second subrange costs over a
            //gigabyte and 4K several times that. Far too much for the little it
            //buys, so the shortfall is filled by cloning the last frame instead.
            double heldSeconds = clipSeconds - available.TotalSeconds;

            graph.FilterLines.Add(
                $"[{index}:v]trim=start={GraphUtilities.Sec(sourceStart)}:" +
                $"end={GraphUtilities.Sec(sourceStart + available)},setpts=PTS-STARTPTS," +
                $"tpad=stop_mode=clone:stop_duration={GraphUtilities.Num(heldSeconds)}," +
                $"trim=duration={GraphUtilities.Num(clipSeconds)},fps={fps}[{label}]");

            return label;
        }

        /// <summary>
        /// Rasterizes the text once with Skia and feeds it in as a still image.
        /// Glyphs are drawn white on transparent so that Clip.Modulate can tint
        /// them — colour lives there rather than on TextClip itself.
        /// </summary>
        private static ClipContent BuildTextClip(
            TextClip clip, InputGraph graph,
            int canvasWidth, int canvasHeight, int fps,
            ConcurrentBag<string> tempFiles)
        {
            string path = TextRasterizer.Rasterize(
                clip, canvasWidth, canvasHeight, out int width, out int height);

            tempFiles.Add(path);

            int index = graph.AddInput(path, true, ["-loop", "1"]);

            string label = graph.NextLabel("cctext");
            graph.FilterLines.Add(
                $"[{index}:v]trim=duration={GraphUtilities.Num(clip.Duration.TotalSeconds)}," +
                $"setpts=PTS-STARTPTS,fps={fps},format={PixelFormats.Primary}[{label}]");

            return new ClipContent(label, width, height, null, false);
        }

        /// <summary>
        /// A flat colour filling the canvas, optionally easing in from one colour
        /// and out to another.
        ///
        /// The clip's steady-state colour is ColorMain. The ramps are built with
        /// xfade between solid sources — a crossfade between two flat colours IS a
        /// linear colour ramp, which avoids needing an animated colour filter
        /// (colorchannelmixer takes constants only, and geq is far too slow to run
        /// per pixel). A null ColorIn/ColorOut means no ramp at that end, so the
        /// clip simply starts or finishes at ColorMain.
        ///
        /// Modulate is an ordinary tint here, exactly as it is for every other clip
        /// type — it multiplies over the generated colour later in the chain rather
        /// than being the generated colour. That is what ColorMain replaced.
        /// </summary>
        private static ClipContent BuildGeneratorClip(
            GeneratorClip clip, InputGraph graph, int canvasWidth, int canvasHeight, int fps)
        {
            double total = clip.Duration.TotalSeconds;
            string size = $"{canvasWidth}x{canvasHeight}";

            string body = graph.NextLabel("ccgenbody");
            graph.FilterLines.Add(
                $"color={Hex(clip.ColorMain)}:size={size}:rate={fps}:" +
                $"duration={GraphUtilities.Num(total)},format={PixelFormats.Primary},settb=AVTB[{body}]");

            string current = body;

            double fadeIn = clip.ColorIn is { } colorIn
                ? Math.Clamp(colorIn.Item2.TotalSeconds, 0, total)
                : 0;

            if (fadeIn > 0)
            {
                string lead = graph.NextLabel("ccgenin");
                graph.FilterLines.Add(
                    $"color={Hex(clip.ColorIn!.Value.Item1)}:size={size}:rate={fps}:" +
                    $"duration={GraphUtilities.Num(fadeIn)},format={PixelFormats.Primary},settb=AVTB[{lead}]");

                //xfade consumes `duration` of overlap, so pairing a fadeIn-long lead
                //with the full-length body leaves the total unchanged
                string faded = graph.NextLabel("ccgenfin");
                graph.FilterLines.Add(
                    $"[{lead}][{current}]xfade=transition=fade:" +
                    $"duration={GraphUtilities.Num(fadeIn)}:offset=0[{faded}]");

                current = faded;
            }

            double fadeOut = clip.ColorOut is { } colorOut
                ? Math.Clamp(colorOut.Item2.TotalSeconds, 0, total - fadeIn)
                : 0;

            if (fadeOut > 0)
            {
                string tail = graph.NextLabel("ccgenout");
                graph.FilterLines.Add(
                    $"color={Hex(clip.ColorOut!.Value.Item1)}:size={size}:rate={fps}:" +
                    $"duration={GraphUtilities.Num(fadeOut)},format={PixelFormats.Primary},settb=AVTB[{tail}]");

                string faded = graph.NextLabel("ccgenfout");
                graph.FilterLines.Add(
                    $"[{current}][{tail}]xfade=transition=fade:" +
                    $"duration={GraphUtilities.Num(fadeOut)}:" +
                    $"offset={GraphUtilities.Num(total - fadeOut)}[{faded}]");

                current = faded;
            }

            return new ClipContent(current, canvasWidth, canvasHeight, null, false);
        }

        /// <summary>
        /// How many noise cells span the canvas at Detail = 1.
        ///
        /// The `perlin` source normalizes its coordinates by frame size —
        /// x = xscale * column / width — so xscale IS the number of noise cells
        /// across the frame, whatever the pixel resolution. That makes Detail
        /// resolution-independent for free: at Detail 1 a feature is 1/1000th of
        /// the canvas at 720p and at 4K alike, rather than 1/1000th at one and a
        /// solid wash at the other.
        /// </summary>
        private const double DetailCellsPerCanvas = 1000.0;

        /// <summary>
        /// Noise cells traversed per second at SeetheRate = 1.
        ///
        /// `perlin` computes its time coordinate as tscale * seconds (pts times
        /// timebase), so this is framerate-independent the same way Detail is
        /// resolution-independent — the noise seethes at the same rate whether the
        /// timeline renders at 24 or 60fps.
        ///
        /// The pattern decorrelates over roughly one cell, so 10 puts SeetheRate 1
        /// at a fast boil and the 0.05 default at a cell every two seconds — a slow
        /// drift. Purely a taste constant; nothing downstream depends on it.
        /// </summary>
        private const double SeetheCellsPerSecond = 10.0;

        /// <summary>
        /// Seething Perlin noise filling the canvas.
        ///
        /// Generated GREYSCALE and left for Clip.Modulate to tint further down the
        /// chain, the same convention TextClip uses — `perlin` emits GRAY8, which
        /// converts to RGBA as an exact neutral grey at full alpha (verified: 0, 128
        /// and 255 all survive the conversion unchanged, so none of the
        /// limited-range trouble that made "transparent" blanks 6% opaque applies
        /// here). White is therefore the identity and any Modulate tint, including
        /// its alpha for a semi-transparent noise layer, works untouched.
        ///
        /// `perlin` is a real libavfilter source, so like `color` it goes inline in
        /// the graph with no -i input of its own. It has no `duration` option and
        /// generates frames indefinitely, so the length has to be taken with a trim
        /// rather than requested up front.
        /// </summary>
        private static ClipContent BuildNoiseClip(
            NoiseClip clip, InputGraph graph, int canvasWidth, int canvasHeight, int fps)
        {
            double total = clip.Duration.TotalSeconds;

            double xscale = Math.Max(clip.Detail, 0f) * DetailCellsPerCanvas;

            //cells are square in PIXELS only if the coordinate span is scaled by the
            //frame's aspect: x runs 0..xscale across the width and y runs 0..yscale
            //across the height, so leaving them equal on a 16:9 canvas would squash
            //every blob vertically
            double yscale = xscale * canvasHeight / (double)canvasWidth;

            double tscale = Math.Max(clip.SeetheRate, 0f) * SeetheCellsPerSecond;

            //random_mode defaults to `random`, which generates its own seed and
            //ignores random_seed entirely — without selecting `seed` mode the Seed
            //property would be silently inert and every render would differ.
            //Cast to uint because the option is unsigned and Seed is a signed int
            //that a caller is free to set negative.
            uint seed = unchecked((uint)clip.Seed);

            string label = graph.NextLabel("ccnoise");
            graph.FilterLines.Add(
                $"perlin=size={canvasWidth}x{canvasHeight}:rate={fps}:" +
                $"random_mode=seed:random_seed={seed}:" +
                $"xscale={GraphUtilities.Num(xscale)}:" +
                $"yscale={GraphUtilities.Num(yscale)}:" +
                $"tscale={GraphUtilities.Num(tscale)}," +
                $"trim=duration={GraphUtilities.Num(total)},setpts=PTS-STARTPTS," +
                $"format={PixelFormats.Primary},settb=AVTB[{label}]");

            return new ClipContent(label, canvasWidth, canvasHeight, null, false);
        }

        /// <summary>
        /// An SKColor as ffmpeg's colour syntax, alpha included.
        ///
        /// The alpha suffix is not optional decoration: `color=0xRRGGBB` is fully
        /// opaque no matter what alpha the SKColor carried, so dropping it made a
        /// semi-transparent ColorMain render solid with no error anywhere. Verified
        /// directly — 0xff0000 gives alpha 255, 0xff0000@0.5 gives 127, and an
        /// xfade ramp between two semi-transparent colours keeps the alpha intact.
        ///
        /// This composes with Clip.Modulate rather than replacing it: the generated
        /// colour's own alpha is multiplied by Modulate's further down the chain,
        /// exactly as its RGB is.
        /// </summary>
        private static string Hex(SKColor colour) =>
            $"0x{colour.Red:x2}{colour.Green:x2}{colour.Blue:x2}" +
            $"@{GraphUtilities.Num(colour.Alpha / 255.0)}";
    }
}
