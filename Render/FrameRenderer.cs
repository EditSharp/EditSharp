using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EditSharp;
using EditSharp.Components;
using EditSharp.Components.Clips;

namespace EditSharp.Render
{
    /// <summary>
    /// Entry point for the frame-by-frame render strategy — this pipeline's only
    /// render path (the old single-filter_complex-per-window approach and its
    /// supporting classes have been removed rather than kept alongside this one).
    ///
    /// Shape of a render:
    ///   1. Build optimized media for every video source clip, upfront, with
    ///      bounded concurrency (OptimizedMediaBuilder).
    ///   2. Prepare everything else that's static across a clip's whole life —
    ///      an Image source's dimensions, a TextClip's ONE rasterized PNG —
    ///      so the per-frame loop never repeats work a clip's own content
    ///      doesn't actually vary frame to frame.
    ///   3. Render every output frame, one ffmpeg process each, appending each
    ///      frame's raw rgba64le bytes to a single growing lossless file.
    ///      Optimized media is deleted the moment no remaining frame needs it.
    ///   4. Mux that lossless video against the timeline's audio (see
    ///      FinalizeOutputAsync) and encode to Blueprint's chosen codec.
    /// </summary>
    public static class FrameRenderer
    {
        public static async Task RenderAsync(Blueprint blueprint)
        {
            Validate(blueprint);

            var tempFiles = new ConcurrentBag<string>();
            var sw = Stopwatch.StartNew();

            EditSharpConfig.Logger.Log("Starting render.");

            try
            {
                await RenderCoreAsync(blueprint, tempFiles);
                EditSharpConfig.Logger.Log($"Render complete in {sw.Elapsed}.");
            }
            finally
            {
                foreach (string path in tempFiles)
                {
                    try { File.Delete(path); } catch { /* best-effort cleanup */ }
                }
            }
        }

        private static async Task RenderCoreAsync(Blueprint blueprint, ConcurrentBag<string> tempFiles)
        {
            IFrameFilterChainBuilder chainBuilder = SelectChainBuilder(blueprint.HardwareAccelerator);

            Timeline timeline = blueprint.Timeline;
            int width = blueprint.Resolution.Item1;
            int height = blueprint.Resolution.Item2;
            int fps = blueprint.Framerate;

            Dictionary<Clip, OptimizedMediaBuilder.OptimizedMedia> optimizedMedia =
                await OptimizedMediaBuilder.BuildAsync(
                    timeline, fps, width, height, tempFiles, blueprint.ExtractionConcurrency);

            foreach (OptimizedMediaBuilder.OptimizedMedia media in optimizedMedia.Values)
                tempFiles.Add(media.Path);

            var nativeSizes = new Dictionary<Clip, (int, int)>();
            var staticImages = new Dictionary<Clip, string>();

            //native size for every VIDEO clip comes straight from the probe
            //OptimizedMediaBuilder already did — no second ffprobe call for
            //what's already known
            foreach ((Clip clip, OptimizedMediaBuilder.OptimizedMedia media) in optimizedMedia)
                nativeSizes[clip] = (media.NativeWidth, media.NativeHeight);

            var staticSw = Stopwatch.StartNew();
            await PrepareStaticContentAsync(
                timeline, width, height, nativeSizes, staticImages, tempFiles);
            EditSharpConfig.Logger.LogVerbose($"Static content prepared in {staticSw.ElapsedMilliseconds}ms.");

            //precomputed once, up front — every clip's Start/Duration/End is
            //already known, so there is nothing to discover at render time.
            //Grouped by frame index rather than scanned per clip per frame: an
            //O(1) dictionary lookup per frame instead of an O(clips) scan
            Dictionary<int, List<string>> deletionSchedule = optimizedMedia.Values
                .GroupBy(m => m.DeleteAfterFrame)
                .ToDictionary(g => g.Key, g => g.Select(m => m.Path).ToList());

            int totalFrames = Math.Max(1, (int)Math.Ceiling(timeline.Duration.TotalSeconds * fps));

            string accumulatorPath = GraphUtilities.GetVideoTempFilePath($"frames_{Guid.NewGuid():N}.raw");
            tempFiles.Add(accumulatorPath);

            EditSharpConfig.Logger.Log(
                $"Rendering {totalFrames} frame(s) at {width}x{height}@{fps}fps " +
                $"(concurrency {blueprint.FrameRenderConcurrency}).");

            using (var accumulator = new FileStream(
                accumulatorPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 1 << 20))
            {
                await RenderAllFramesAsync(
                    timeline, fps, width, height, chainBuilder, optimizedMedia, nativeSizes,
                    staticImages, deletionSchedule, totalFrames,
                    blueprint.FrameRenderConcurrency, accumulator, tempFiles);
            }

            EditSharpConfig.Logger.Log("Finalizing output (mux + encode)...");
            var finalizeSw = Stopwatch.StartNew();
            await FinalizeOutputAsync(accumulatorPath, width, height, fps, blueprint, tempFiles);
            EditSharpConfig.Logger.Log($"Finalize complete in {finalizeSw.ElapsedMilliseconds}ms.");
        }

        /// <summary>
        /// Renders every output frame, up to FrameRenderConcurrency at once, and
        /// writes each one to the accumulator STRICTLY in order.
        ///
        /// Every frame's task is launched immediately; the SemaphoreSlim inside
        /// RenderOneAsync is what actually bounds how many are mid-render at
        /// once, not this method. Frames can finish rendering out of order once
        /// more than one is in flight, but two things both require frame i to
        /// be fully settled before frame i+1 is acted on: the accumulator is a
        /// headerless raw stream with no per-frame timestamps, so writes must
        /// land in order; and a clip's optimized media must not be deleted
        /// until every frame that could still need it — up to and including its
        /// DeleteAfterFrame — has actually finished rendering, not merely been
        /// scheduled. Awaiting frameTasks[frameIndex] in a plain increasing loop
        /// satisfies both for free, and — just as importantly — surfaces a
        /// fault on any frame the moment that frame's turn comes up. An earlier
        /// version of this method buffered completions into a side dictionary
        /// and only reached its final Task.WhenAll after every frame had been
        /// LAUNCHED, which meant a frame 0 exception sat unobserved for the
        /// entire render and looked identical to a hang.
        /// </summary>
        private static async Task RenderAllFramesAsync(
            Timeline timeline, int fps, int width, int height,
            IFrameFilterChainBuilder chainBuilder,
            Dictionary<Clip, OptimizedMediaBuilder.OptimizedMedia> optimizedMedia,
            Dictionary<Clip, (int, int)> nativeSizes,
            Dictionary<Clip, string> staticImages,
            Dictionary<int, List<string>> deletionSchedule,
            int totalFrames, int concurrency,
            Stream accumulator, ConcurrentBag<string> tempFiles)
        {
            using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
            var frameTasks = new Task<(byte[] Buffer, int Length)>[totalFrames];
            var sw = Stopwatch.StartNew();

            //frame 0 alone can't tell us whether per-frame seek cost GROWS
            //with how far into the source the target timestamp is — its own
            //seek offset is always near zero. A second checkpoint partway
            //through gives ffmpeg's own decode_video bench numbers something
            //to compare against: if seeking without a usable Cues index falls
            //back to an O(n) forward scan, the SAME clips' decode_video "real"
            //time at this checkpoint should be visibly larger than at frame 0.
            //100 if the render is that long, otherwise roughly the midpoint —
            //either way, meaningfully further into the source than frame 0.
            int benchmarkCheckpoint = Math.Min(100, totalFrames / 2);

            async Task<(byte[] Buffer, int Length)> RenderOneAsync(int frameIndex)
            {
                await gate.WaitAsync();
                try
                {
                    //logged the moment this frame actually gets a gate slot and
                    //begins building its graph — if a render appears stuck, this
                    //line (or its absence) is what tells you whether it's stuck
                    //BEFORE ffmpeg is even spawned (graph construction) or DURING
                    //ffmpeg's own run (no further logs after this one)
                    EditSharpConfig.Logger.LogVerbose(
                        $"Starting frame {frameIndex + 1}/{totalFrames}...");

                    FrameState state = FrameStateResolver.Resolve(
                        timeline, frameIndex, fps, optimizedMedia, nativeSizes, staticImages);

                    bool benchmark = frameIndex == 0 || frameIndex == benchmarkCheckpoint;

                    (byte[] Buffer, int Length) result = await RenderFrameAsync(
                        state, chainBuilder, width, height, fps, tempFiles, benchmark);

                    if (frameIndex == benchmarkCheckpoint)
                    {
                        //diagnostic only — re-renders frame 0's EXACT content
                        //again, this late in the render, and discards the
                        //output (never written to the accumulator, never
                        //counted as a real frame). The comparison this buys:
                        //if THIS takes roughly frame 0's original time, the
                        //checkpoint's slowdown is driven by that frame's own
                        //content (e.g. a more expensive resolved transform);
                        //if it's roughly as slow as the REAL checkpoint frame
                        //instead, the slowdown is driven by something that
                        //accumulates over the render's lifetime — process
                        //count, disk/AV state, memory fragmentation — since
                        //the content here is IDENTICAL to frame 0's and only
                        //WHEN it runs differs
                        EditSharpConfig.Logger.Log(
                            $"Diagnostic: replaying frame 0's content at position " +
                            $"{frameIndex} for comparison...");

                        FrameState replay = FrameStateResolver.Resolve(
                            timeline, 0, fps, optimizedMedia, nativeSizes, staticImages);

                        (byte[] Buffer, int Length) discarded = await RenderFrameAsync(
                            replay, chainBuilder, width, height, fps, tempFiles, benchmark: true);

                        //this one is never written anywhere — return it to the
                        //pool immediately rather than leaving it for the GC,
                        //same as every real frame's buffer does after it's
                        //written to the accumulator below
                        ArrayPool<byte>.Shared.Return(discarded.Buffer);
                    }

                    return result;
                }
                finally
                {
                    gate.Release();
                }
            }

            //every frame's task is launched up front — the gate above, not this
            //loop, is what bounds how many are actually mid-render at once, so
            //launching them all costs nothing but Task allocation for the ones
            //still queued on the gate
            for (int frameIndex = 0; frameIndex < totalFrames; frameIndex++)
                frameTasks[frameIndex] = RenderOneAsync(frameIndex);

            for (int frameIndex = 0; frameIndex < totalFrames; frameIndex++)
            {
                //awaited strictly in order. This is what the accumulator's
                //no-timestamps format requires regardless, but it also means a
                //fault on any frame surfaces the moment that frame's turn comes
                //up — NOT buried until every one of totalFrames tasks has been
                //launched and Task.WhenAll is finally reached, which is what an
                //earlier version of this method did and which silently hid a
                //frame 0 failure behind what looked like a hang
                (byte[] buffer, int length) = await frameTasks[frameIndex];

                //frameTasks retaining a completed Task's Result (its buffer)
                //for the whole render was a REAL bug, fixed by clearing this
                //slot the moment it's consumed — but it turned out not to be
                //the actual cause of the progressive slowdown. The real cause
                //(confirmed via Process resource sampling — handles/threads/
                //managed heap all flat, but GC gen0≈gen1≈gen2 climbing in
                //lockstep, meaning EVERY collection was a full gen2 sweep) was
                //that each frame's ~16.6MB buffer is a Large Object Heap
                //allocation, and the LOH is only ever reclaimed as part of a
                //gen2 collection — so a fresh LOH allocation on literally every
                //frame forced a full heap scan every time, regardless of GC
                //mode (confirmed: enabling Server GC didn't help either, since
                //the trigger is the allocation pattern itself, not which GC
                //flavor is handling it). RenderFrameAsync now rents this
                //buffer from ArrayPool<byte>.Shared instead of allocating
                //fresh via MemoryStream.ToArray() every frame — Return() right
                //after use is what makes the pool actually reusable rather
                //than degrading into the same one-fresh-allocation-per-frame
                //pattern this was meant to fix.
                frameTasks[frameIndex] = null!;

                try
                {
                    await accumulator.WriteAsync(buffer.AsMemory(0, length));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                EditSharpConfig.Logger.LogVerbose(
                    $"Rendered frame {frameIndex + 1}/{totalFrames} " +
                    $"({sw.ElapsedMilliseconds}ms elapsed).");

                if (deletionSchedule.TryGetValue(frameIndex, out List<string>? exhausted))
                {
                    foreach (string path in exhausted)
                    {
                        try { File.Delete(path); } catch { /* best-effort */ }
                    }
                }

                //diagnostic: every 20 frames, sample the .NET process's own
                //resource counters. This is aimed squarely at ruling in or
                //out a specific, well-documented class of bug — repeated
                //Process.Start()/Dispose() cycles can leak OS handles or
                //cause managed-heap growth independent of anything the
                //CHILD process does — which is exactly what a render that
                //degrades identically on pure-procedural content (zero
                //decode, zero file I/O, zero transform math) with nothing
                //else varying would look like. If HandleCount or ThreadCount
                //climbs in step with the slowdown, that's the leak. If GC
                //gen2/LOH collection counts climb disproportionately (not
                //just gen0, which is normal and constant), that's managed
                //memory pressure building up somewhere still unaccounted
                //for. Sampled here rather than in a background timer so the
                //numbers line up exactly against the frame index they were
                //taken at.
                if (frameIndex % 20 == 0)
                {
                    try
                    {
                        using Process self = Process.GetCurrentProcess();
                        self.Refresh();

                        EditSharpConfig.Logger.Log(
                            $"Resource sample @ frame {frameIndex + 1}: " +
                            $"handles={self.HandleCount}, threads={self.Threads.Count}, " +
                            $"managed heap={GC.GetTotalMemory(false) / (1024 * 1024)}MB, " +
                            $"workingSet={self.WorkingSet64 / (1024 * 1024)}MB, " +
                            $"GC(gen0/1/2)={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
                    }
                    catch
                    {
                        //diagnostic only — never worth failing a render over
                    }
                }
            }
        }

        /// <summary>
        /// Picks the filter-chain construction strategy for Blueprint.
        /// HardwareAccelerator. Only None (software, the stock CPU filter
        /// chain) is implemented — the GPU/libplacebo chain is a paused,
        /// separate effort (see the project notes) and is deliberately NOT
        /// silently downgraded to software the way the old whole-window
        /// pipeline downgrades an unavailable NVENC encoder: a render that
        /// asked for hardware and got software instead, with no error, is
        /// exactly the kind of silent behaviour change worth failing loudly on
        /// instead.
        /// </summary>
        private static IFrameFilterChainBuilder SelectChainBuilder(HardwareAccelerator accelerator) =>
            accelerator switch
            {
                HardwareAccelerator.None => new SoftwareFrameFilterChainBuilder(),
                HardwareAccelerator.Nvenc => throw new NotImplementedException(
                    "Frame-by-frame rendering with HardwareAccelerator.Nvenc (the GPU/libplacebo " +
                    "filter chain) is not implemented yet. Use HardwareAccelerator.None."),
                _ => throw new NotSupportedException($"Unknown HardwareAccelerator value: {accelerator}."),
            };

        /// <summary>
        /// Everything about a clip that's constant across its whole life and
        /// would otherwise be redone on every frame it's visible on: an Image
        /// source's dimensions, and a TextClip's rasterized PNG (built exactly
        /// ONCE here rather than once per frame — Content never changes mid-clip,
        /// so re-rasterizing identical text for every visible frame would be
        /// pure waste).
        ///
        /// SourceClip video is deliberately absent — its native size comes from
        /// OptimizedMediaBuilder's own probe, and GeneratorClip/NoiseClip need
        /// no entry at all since SoftwareFrameFilterChainBuilder sizes them to
        /// the canvas directly.
        /// </summary>
        private static Task PrepareStaticContentAsync(
            Timeline timeline, int canvasWidth, int canvasHeight,
            Dictionary<Clip, (int, int)> nativeSizes,
            Dictionary<Clip, string> staticImages,
            ConcurrentBag<string> tempFiles)
        {
            var tasks = new List<Task>();

            foreach (Channel channel in timeline.Channels)
            {
                foreach (Clip clip in channel.Clips.Values)
                {
                    switch (clip)
                    {
                        case SourceClip { Source.Type: SourceType.Image } imageClip:
                            tasks.Add(PrepareImageAsync(clip, imageClip, nativeSizes, staticImages));
                            break;

                        case TextClip text:
                            PrepareText(clip, text, canvasWidth, canvasHeight,
                                nativeSizes, staticImages, tempFiles);
                            break;
                    }
                }
            }

            return Task.WhenAll(tasks);
        }

        private static async Task PrepareImageAsync(
            Clip clip, SourceClip imageClip,
            Dictionary<Clip, (int, int)> nativeSizes, Dictionary<Clip, string> staticImages)
        {
            (int width, int height) = await MediaProbe.GetDimensionsAsync(imageClip.Source.Path);
            nativeSizes[clip] = (width, height);
            staticImages[clip] = imageClip.Source.Path;
        }

        private static void PrepareText(
            Clip clip, TextClip text, int canvasWidth, int canvasHeight,
            Dictionary<Clip, (int, int)> nativeSizes, Dictionary<Clip, string> staticImages,
            ConcurrentBag<string> tempFiles)
        {
            string path = TextRasterizer.Rasterize(
                text, canvasWidth, canvasHeight, out int width, out int height);

            tempFiles.Add(path);
            nativeSizes[clip] = (width, height);
            staticImages[clip] = path;
        }

        /// <summary>
        /// Renders one output frame: builds its filter graph and runs ffmpeg
        /// with -frames:v 1 writing raw rgba64le to stdout, buffered in memory
        /// and handed back to the caller rather than written straight to a
        /// shared accumulator stream — RenderAllFramesAsync may have several of
        /// these in flight at once, and a FileStream can't be written by
        /// multiple callers concurrently. The caller is responsible for
        /// flushing the bytes to the accumulator in frame order.
        ///
        /// The filter_complex is passed inline rather than through the
        /// "-/filter_complex &lt;file&gt;" mechanism RunFfmpegAsync uses for the
        /// whole-window pipeline. A per-frame graph is bounded by how many
        /// clips can be simultaneously visible (at most a handful per channel,
        /// two mid-transition), nothing like the size a whole timeline's graph
        /// reaches — and avoiding a temp file per frame matters here, since
        /// this runs once per output frame rather than once per render. If a
        /// project ever produces per-frame graphs large enough to hit the OS
        /// command-line limit, switch this call site to the same script-file
        /// trick.
        /// </summary>
        /// <summary>
        /// Renders one frame and returns its raw pixel bytes in a buffer
        /// RENTED from ArrayPool&lt;byte&gt;.Shared, along with the exact number
        /// of bytes actually used (buffer.Length may be larger — the pool
        /// rounds up to its own bucket sizes). The caller MUST call
        /// ArrayPool&lt;byte&gt;.Shared.Return(buffer) once done with it, on
        /// every path including error/discard, or the pool degrades back
        /// into fresh-allocation-per-frame.
        ///
        /// This used to accumulate into a MemoryStream and return
        /// stream.ToArray() — a fresh, exactly-trimmed allocation every
        /// single frame. At 1920x1080 gbrap16le that's ~16.6MB, comfortably
        /// past .NET's 85KB Large Object Heap threshold, and the LOH is only
        /// ever reclaimed as part of a full gen2 collection — so a fresh LOH
        /// allocation on literally every frame forced a full heap scan every
        /// time. Confirmed directly via Process resource sampling on a real
        /// render (handles/threads/managed-heap-size all flat across the
        /// render, but gen0/gen1/gen2 GC counts climbing in lockstep — i.e.
        /// EVERY collection was promoted to a full gen2 sweep) and confirmed
        /// NOT to be a GC-flavor issue (Server GC made no difference — the
        /// allocation pattern itself is the trigger, not which GC handles
        /// it). Renting the same handful of buffers across hundreds of
        /// frames instead of allocating fresh every time removes the
        /// trigger entirely.
        ///
        /// Reading directly into an exactly-sized buffer, rather than a
        /// growable MemoryStream, is possible because raw video output at a
        /// fixed resolution and pixel format has a fully deterministic byte
        /// count (see PixelFormats.PrimaryBytesPerPixel) — the same
        /// assumption FinalizeOutputAsync's accumulator read already relies
        /// on. The trailing zero-byte-probe read is a safety check on that
        /// assumption: if ffmpeg ever produces more than the computed size
        /// (e.g. because the format's actual byte layout doesn't match what
        /// PrimaryBytesPerPixel assumes), this fails loudly rather than
        /// silently truncating a frame.
        /// </summary>
        private static async Task<(byte[] Buffer, int Length)> RenderFrameAsync(
            FrameState state, IFrameFilterChainBuilder chainBuilder,
            int width, int height, int fps,
            ConcurrentBag<string> tempFiles, bool benchmark)
        {
            var stageSw = Stopwatch.StartNew();

            var graph = new InputGraph();
            string finalLabel = chainBuilder.Build(state, graph, width, height, fps, tempFiles);

            foreach (var input in graph.Inputs)
            {
                if (input.VerifyExists && !File.Exists(input.Path))
                    throw new FileNotFoundException(
                        $"Input file not found rendering frame {state.FrameIndex}: {input.Path}",
                        input.Path);
            }

            long graphBuildMs = stageSw.ElapsedMilliseconds;
            stageSw.Restart();

            //runs with ffmpeg's own -benchmark_all so it prints a decode/
            //encode/flush timing breakdown to stderr, instead of the manual-
            //command approach (which needs the filter_complex string
            //re-quoted for a shell and evidently doesn't survive that
            //intact). -v info rather than error is required for
            //-benchmark_all's output to actually appear. The caller decides
            //which frame indices this fires on (see RenderAllFramesAsync) —
            //frame 0 plus a later checkpoint, so decode_video's "real" time
            //for the SAME clips can be compared at a near-zero seek offset
            //against a much larger one, to test whether seek cost grows with
            //how far into the source the target timestamp is.

            var args = benchmark
                ? new List<string> { "-y", "-v", "info", "-benchmark_all" }
                : new List<string> { "-y", "-v", "error" };

            foreach (var input in graph.Inputs)
            {
                if (input.ExtraArgs != null) args.AddRange(input.ExtraArgs);
                args.Add("-i");
                args.Add(input.Path);
            }

            args.Add("-filter_complex");
            args.Add(string.Join(";", graph.FilterLines));

            args.Add("-map");
            args.Add($"[{finalLabel}]");

            args.Add("-frames:v");
            args.Add("1");
            args.Add("-f");
            args.Add("rawvideo");
            args.Add("-pix_fmt");
            args.Add(PixelFormats.Primary);
            args.Add("pipe:1");

            var psi = new ProcessStartInfo
            {
                FileName = EditSharpConfig.FfmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi };
            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            //timed separately from graph build above and from the read below
            //so a slow frame can be attributed to one of three distinct causes:
            //.NET building the args/graph, the OS actually getting the process
            //running (process.Start() returning), or ffmpeg's own filter
            //execution (everything from Start() to the read finishing)
            var spawnSw = Stopwatch.StartNew();
            process.Start();
            long spawnMs = spawnSw.ElapsedMilliseconds;

            process.BeginErrorReadLine();

            //deterministic byte count for raw video at a fixed resolution and
            //pixel format — see PixelFormats.PrimaryBytesPerPixel and this
            //method's own remarks for why this replaces a growable
            //MemoryStream.ToArray() per frame. buffer.Length may exceed
            //expectedBytes (ArrayPool rounds up to its own bucket sizes);
            //expectedBytes is the caller's contract for how much of it is
            //actually valid.
            int expectedBytes = width * height * PixelFormats.PrimaryBytesPerPixel;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(expectedBytes);

            long runMs;
            try
            {
                //drained concurrently with stderr rather than read after exit —
                //the same deadlock risk MediaProbe's own comments describe: a
                //full pipe buffer blocks the child if nothing is consuming the
                //other one
                var runSw = Stopwatch.StartNew();

                Stream pipe = process.StandardOutput.BaseStream;
                int totalRead = 0;
                while (totalRead < expectedBytes)
                {
                    int read = await pipe.ReadAsync(buffer.AsMemory(totalRead, expectedBytes - totalRead));
                    if (read == 0)
                    {
                        await process.WaitForExitAsync();
                        throw new InvalidOperationException(
                            $"ffmpeg produced only {totalRead} of the expected {expectedBytes} " +
                            $"bytes for frame {state.FrameIndex} before closing its output pipe " +
                            $"(exit code {process.ExitCode}):\n{stderr}");
                    }
                    totalRead += read;
                }

                //one more read past the expected size: if this returns
                //anything at all, PrimaryBytesPerPixel's assumption about
                //this format's byte layout is wrong for this stream, and
                //silently keeping only the first expectedBytes would produce
                //a corrupted frame rather than a loud failure
                Memory<byte> probe = new byte[1];
                int extra = await pipe.ReadAsync(probe);
                if (extra > 0)
                {
                    //ffmpeg is still writing and nothing will drain it further
                    //once this throws — left alone it would sit blocked on a
                    //full pipe forever rather than exiting. Best-effort: this
                    //branch is only reachable if the byte-size assumption
                    //above is actually wrong, so a failed kill isn't worth
                    //failing over
                    try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }

                    throw new InvalidOperationException(
                        $"ffmpeg produced MORE than the expected {expectedBytes} bytes for " +
                        $"frame {state.FrameIndex} — PixelFormats.PrimaryBytesPerPixel's " +
                        $"assumption (width*height*{PixelFormats.PrimaryBytesPerPixel}) doesn't " +
                        $"match this stream's actual byte layout.");
                }

                await process.WaitForExitAsync();
                runMs = runSw.ElapsedMilliseconds;
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }

            if (process.ExitCode != 0)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw new InvalidOperationException(
                    $"ffmpeg exited with code {process.ExitCode} rendering frame " +
                    $"{state.FrameIndex}:\n{stderr}");
            }

            //on the plain -v error frames stderr is normally empty and silently
            //discarded — but this run asked ffmpeg for -benchmark_all, and that
            //output only exists in stderr, so it has to be surfaced here or the
            //whole point of running with it is lost
            if (benchmark)
            {
                EditSharpConfig.Logger.Log(
                    $"Frame {state.FrameIndex} ffmpeg -benchmark_all output " +
                    $"(decode/encode/flush timing):\n{stderr}");
            }

            EditSharpConfig.Logger.LogVerbose(
                $"Frame {state.FrameIndex + 1}: graph build {graphBuildMs}ms, " +
                $"process spawn {spawnMs}ms, ffmpeg run {runMs}ms " +
                $"({graph.Inputs.Count} input(s), {graph.FilterLines.Count} filter line(s)).");

            return (buffer, expectedBytes);
        }

        /// <summary>
        /// Muxes the accumulated lossless video against the timeline's audio
        /// and encodes to Blueprint's chosen codec — the one lossy step in the
        /// whole pipeline, and only when the codec itself is lossy.
        ///
        /// Audio is built the same way it always has in this pipeline: a fresh
        /// InputGraph, ClipContentBuilder per clip, AudioMixer.Compose — none of
        /// that changed when the video side moved to frame-by-frame. Worth
        /// knowing: ClipContentBuilder builds each
        /// clip's VIDEO label too, even though only the audio one is used here
        /// — those filter lines are simply never mapped to output, which
        /// ffmpeg tolerates, but it does mean a wasted decode per video clip
        /// during this pass. Not fixed here; would need an audio-only mode on
        /// ClipContentBuilder to avoid.
        /// </summary>
        private static async Task FinalizeOutputAsync(
            string accumulatorPath, int width, int height, int fps,
            Blueprint blueprint, ConcurrentBag<string> tempFiles)
        {
            var audioGraph = new InputGraph();
            var contents = new Dictionary<Clip, ClipContent>();

            //The accumulator occupies -i index 0 below, so it has to occupy
            //index 0 in THIS graph too before any clip is built. InputGraph
            //hands out indices in call order and ClipContentBuilder bakes them
            //straight into its filter labels ([N:v]/[N:a]), so without this
            //placeholder the first clip's source would take index 0 and every
            //label it emits would resolve against the accumulator instead —
            //which is a headerless rawvideo stream with a video track and no
            //audio whatsoever, producing exactly "Stream specifier ':a' ...
            //matches no streams" at bind time.
            //
            //verifyExists:false because the accumulator is re-registered in
            //the -i list explicitly (with its own rawvideo/-s/-r args) rather
            //than emitted from this collection; this entry exists purely to
            //consume index 0 so the numbering lines up.
            _ = audioGraph.AddInput(accumulatorPath, verifyExists: false);

            foreach (Channel channel in blueprint.Timeline.Channels)
            {
                foreach (Clip clip in channel.Clips.Values)
                {
                    if (contents.ContainsKey(clip)) continue;

                    contents[clip] = await ClipContentBuilder.BuildAsync(
                        clip, audioGraph, width, height, fps, tempFiles, audioOnly: true);
                }
            }

            string audioLabel = AudioMixer.Compose(blueprint.Timeline, contents, audioGraph);

            bool isGif = blueprint.VideoCodec == VideoCodec.GIF;
            (string videoEncoderName, List<string> videoQualityArgs) =
                await FfmpegRunner.GetVideoEncoderSettingsAsync(
                    blueprint.VideoCodec, blueprint.HardwareAccelerator);

            string filterComplex = string.Join(";", audioGraph.FilterLines);
            string scriptPath = GraphUtilities.GetVideoTempFilePath($"audiofilter_{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(scriptPath, filterComplex);

            var args = new List<string> { "-y", "-v", "error" };

            //input 0: the accumulated frames. Headerless raw data, so every
            //dimension ffmpeg would normally read from a container header has
            //to be told explicitly instead
            args.AddRange(new[]
            {
                "-f", "rawvideo",
                "-pix_fmt", PixelFormats.Primary,
                "-s", $"{width}x{height}",
                "-r", fps.ToString(CultureInfo.InvariantCulture),
                "-i", accumulatorPath,
            });

            //the audio graph's own inputs land at indices 1..N — index 0 is
            //the placeholder reserved above for the accumulator, which was
            //already emitted explicitly with its rawvideo args, so it's
            //skipped here rather than added a second time
            foreach (var input in audioGraph.Inputs.Skip(1))
            {
                if (input.ExtraArgs != null) args.AddRange(input.ExtraArgs);
                args.Add("-i");
                args.Add(input.Path);
            }

            args.Add("-/filter_complex");
            args.Add(scriptPath);

            args.Add("-map");
            args.Add("0:v");

            if (!isGif)
            {
                args.Add("-map");
                args.Add($"[{audioLabel}]");
            }

            if (isGif)
            {
                args.Add("-c:v");
                args.Add("gif");
            }
            else
            {
                args.Add("-c:v");
                args.Add(videoEncoderName);
                args.AddRange(videoQualityArgs);

                string audioCodecName = Constants.AudioCodecNames[blueprint.AudioCodec];
                args.Add("-c:a");
                args.Add(audioCodecName);
                args.Add("-b:a");
                args.Add("192k");
                args.Add("-shortest");
                args.Add("-pix_fmt");
                args.Add("yuv420p");
            }

            args.Add(blueprint.OutputDirectory);

            var psi = new ProcessStartInfo
            {
                FileName = EditSharpConfig.FfmpegPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"ffmpeg exited with code {process.ExitCode} finalizing output:\n{stderr}\n\n" +
                    $"Audio filter script preserved for inspection at: {scriptPath}");

            try { File.Delete(scriptPath); } catch { /* best-effort cleanup */ }
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
            // through everything beneath it for the length of the transition —
            // FrameFilterChain.ApplyTransition drives the same xfade transition
            // types this checks against, so the failure mode is identical to the
            // old whole-window pipeline's. Harmless on the bottom channel, which is
            // flattened onto black anyway.
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
