using System;
using System.Diagnostics;
using System.IO;
using SkiaSharp;
using EditSharp.Components;

namespace EditSharp.Render
{
    /// <summary>
    /// Item 11, decided in conversation: source video decode is now a
    /// single ffmpeg subprocess per active SourceClip, piping raw decoded
    /// frames directly to the Skia compositor as they're consumed — no
    /// pre-rendered seekable intermediate file, no OptimizedMediaBuilder
    /// pre-render pass for video sources.
    ///
    /// WHY THIS IS SAFE NOW, WHEN IT WASN'T BEFORE: OptimizedMediaBuilder's
    /// pre-render existed to give independent per-frame ffmpeg processes
    /// fast RANDOM-ACCESS seeks into source content — each of those
    /// processes needed to jump to an arbitrary point in time with no
    /// relationship to what the process before it had done. That
    /// requirement is gone: the render is now one long-lived process
    /// walking output frames in strict increasing time order (decided in
    /// conversation: FrameRenderConcurrency — parallel OUTPUT frames — is
    /// dropped entirely, precisely because it's incompatible with a single
    /// ordered pipe per source; validate whether single-threaded Skia
    /// throughput is fast enough WITHOUT it at item 14's real timing pass,
    /// not assumed here). A clip's own decode stream therefore only ever
    /// needs to move FORWARD — exactly what a pipe naturally provides, for
    /// free, with no seek at all past the one-time initial `-ss` into the
    /// clip's start offset within its source file.
    ///
    /// NOT AFFECTED BY THIS ITEM: decoding multiple DIFFERENT sources
    /// concurrently for the SAME output frame — each source already gets
    /// its own independent subprocess/pipe, and those already run in
    /// parallel with each other regardless of piping. Only PARALLEL FRAMES
    /// (multiple output frames in flight at once, each wanting its own
    /// position in the same source's timeline simultaneously) conflict with
    /// a single ordered pipe — that's the capability being traded away.
    ///
    /// THE FPS-CONFORMANCE TRICK that makes per-frame consumption trivial:
    /// the source stream is requested from ffmpeg ALREADY resampled to the
    /// render's own output fps (via `-r`/`fps=`, using ffmpeg's own
    /// frame-duplication/drop logic — not reimplemented here). Once decode
    /// fps matches render fps exactly, "the next rawvideo frame in the
    /// pipe" IS "the next output frame index" — a straight sequential read,
    /// no per-frame timestamp matching needed in C# at all.
    ///
    /// TRADE-OFF, named not hidden: optimized media also did double duty as
    /// a place to pre-bake PreTransform effects and pre-scale oversized
    /// sources once rather than per frame (see FrameStateResolver's old
    /// PreTransformEffectsBaked flag, and SkiaClipCompositorSketch.Composite's
    /// preTransformEffectsBaked parameter — both become permanently
    /// irrelevant for SourceClip now, not removed from those files in this
    /// pass). Every frame now pays full native-resolution decode + full
    /// PreTransform effect cost live, where some of that was previously a
    /// one-time cost. Not a blocker, a real cost worth having on the radar.
    ///
    /// FAST-OPEN (fastOpen), ADDED AFTER DIRECT FEEDBACK THAT SCRUBBING WAS
    /// AS SLOW AS A FRESH SESSION START: opening ANY ffmpeg decode process
    /// pays avformat_find_stream_info's own default probing cost before it
    /// can produce a single frame — by default ffmpeg reads/analyzes a
    /// real chunk of the input to work out stream parameters it doesn't
    /// yet know, regardless of how simple the file actually is. For a
    /// clip's own original source that's the right default (camera/delivery
    /// files can have unusual or variable-rate streams worth actually
    /// analyzing). It is pure waste for a file THIS codebase built itself
    /// via OptimizedMediaCache — single all-intra video stream, no audio,
    /// known container, already probed once at build time — and it's paid
    /// again on every fresh open, which for scrubbing means every single
    /// ScrubToAsync tick (see that method's own remarks on why it can't
    /// reuse one open decoder). `fastOpen` shrinks -probesize/-analyzeduration
    /// down to the minimum for exactly that known-good case; see
    /// SkClipContentSource.GetOrOpenDecoder for how it decides when to pass
    /// this (true only when the file actually being opened is an
    /// OptimizedMediaCache entry, never the clip's own original source).
    /// </summary>
    internal sealed class SkSourceDecoder : IDisposable
    {
        private readonly Process _process;
        private readonly Stream _stdout;
        private readonly int _width;
        private readonly int _height;
        private readonly int _frameByteSize;
        private byte[]? _lastFrameBytes;
        private bool _exhausted;

        private SkSourceDecoder(Process process, Stream stdout, int width, int height)
        {
            _process = process;
            _stdout = stdout;
            _width = width;
            _height = height;
            _frameByteSize = width * height * 4; // rgba8888 — see class remarks on format choice
        }

        /// <summary>
        /// Starts decoding `sourcePath` from `sourceStartSeconds` (the
        /// clip's own trim offset into the file — ONE seek here, paid once
        /// at stream setup, categorically different from the old per-frame
        /// seek cost this replaces), conformed to `fps`, at `width`x`height`
        /// — the DECODE TARGET (see SkClipContentSource.GetOrOpenDecoder),
        /// which is the clip's own max-scale content size capped at native
        /// resolution, NOT unconditionally native resolution the way this
        /// used to work. `plan` (see DecodeHwAccelPlan) resolves both the
        /// -hwaccel args AND which scale filter (GPU or CPU) builds the
        /// actual -vf string — defaults to DecodeHwAccelPlan.Software.
        /// `fastOpen` shrinks ffmpeg's own stream-probing cost — see the
        /// class remarks' FAST-OPEN section; only pass true for a file this
        /// codebase built and controls the shape of.
        ///
        /// Output pixel format is rgba8888 — NOT PixelFormats.Primary
        /// (gbrap16le). That 16-bit choice was explicitly tied to the OLD
        /// many-independent-process pipeline's compounding quantization
        /// concern (see item 12, not yet decided) and doesn't obviously
        /// apply to a single long-lived process; rgba8888 is what an
        /// SKSurface/SKImage consumes directly with no extra conversion
        /// step. This anticipates item 12's likely direction without fully
        /// deciding bit depth here — flagged, not silently assumed final.
        /// </summary>
        public static SkSourceDecoder Start(
            string sourcePath, double sourceStartSeconds, int fps, int width, int height,
            DecodeHwAccelPlan? plan = null, bool fastOpen = false)
        {
            plan ??= DecodeHwAccelPlan.Software;
            string filter = plan.BuildFilterGraph(fps, width, height);

            var args = new System.Collections.Generic.List<string>
            {
                "-y", "-v", "error",
            };

            args.AddRange(GraphUtilities.FilterThreadingArgs());
            args.AddRange(plan.HwAccelArgs);

            if (fastOpen)
            {
                // See the class remarks' FAST-OPEN section. 32 KiB is
                // enough for ffmpeg to see this file's own moov atom
                // (already moved to the front by OptimizedMediaCache's
                // +faststart — see its EncodeAsync remarks) and a handful
                // of sample entries; analyzeduration 0 tells it to rely on
                // probesize alone rather than also reading a slice of
                // actual stream duration to cross-check timing, which is
                // exactly the extra read this is trying to skip.
                args.Add("-probesize");
                args.Add("32k");
                args.Add("-analyzeduration");
                args.Add("0");
            }

            if (sourceStartSeconds > 0)
            {
                args.Add("-ss");
                args.Add(GraphUtilities.Num(sourceStartSeconds));
            }

            args.AddRange(new[]
            {
                "-i", sourcePath,
                "-filter:v", filter,
                "-f", "rawvideo",
                "-pix_fmt", "rgba",
                "-an",
                "pipe:1",
            });

            var psi = new ProcessStartInfo
            {
                FileName = EditSharpConfig.FfmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            var sw = Stopwatch.StartNew();
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Start();

            // TEMPORARY INSTRUMENTATION — process spawn time alone (before
            // any frame has been read), split out from NextFrame's own
            // cumulative pipe-read timing, specifically to answer "is
            // ScrubToAsync's per-tick cost dominated by spawning ffmpeg
            // itself, or by everything after (stream probing, seek, first
            // decode)" — added after direct feedback that scrubbing was as
            // slow as a fresh session start. Remove once fastOpen/+faststart
            // are confirmed sufficient or a further fix replaces this.
            EditSharpConfig.Logger.LogVerbose(
                $"SkSourceDecoder.Start('{sourcePath}', fastOpen={fastOpen}): process spawned in " +
                $"{sw.ElapsedMilliseconds}ms (stream probing/seek/first-frame cost is NOT included — " +
                "see NextFrame's own timing for that).");

            // Deliberately NOT reading stderr asynchronously the way
            // NoiseRenderer/OptimizedMediaBuilder do for a short-lived
            // build step — this process lives for the clip's whole visible
            // duration. A caller that wants stderr surfaced on failure
            // should read process.StandardError itself; left unbuffered
            // here rather than accumulating an unbounded StringBuilder for
            // a long-lived process. Flagged, not silently decided as fine
            // for every use case.
            return new SkSourceDecoder(process, process.StandardOutput.BaseStream, width, height);
        }

        /// <summary>
        /// Advances to and returns the next frame in this clip's own
        /// sequential timeline. Must be called exactly once per output
        /// frame this clip is visible on, in increasing order — this is
        /// the "sequentially forward" contract the whole design depends on;
        /// calling it out of order or skipping frames desyncs this
        /// decoder's position from the render's own frame index.
        ///
        /// FREEZE-FRAME behaviour on source exhaustion, matching the old
        /// optimized-media path's documented behaviour exactly: once the
        /// pipe runs out (this clip's Duration outlasts its source), the
        /// last successfully decoded frame is returned again for every
        /// further call, rather than throwing or returning null.
        /// </summary>
        // Cumulative time spent blocked on the pipe read, across every call —
        // reported alongside each per-call time so both "is this frame slow"
        // and "is decode slow overall" are visible without re-deriving from
        // a series of per-call numbers by hand.
        private TimeSpan _cumulativeReadTime = TimeSpan.Zero;

        /// <summary>
        /// TEMPORARY INSTRUMENTATION — added specifically to isolate the
        /// video-clip blueprint's per-frame cost, which was disproportionately
        /// worse than every shader/generator-only blueprint even after the
        /// GPU compositing fix (that fix touches Skia's own draw calls only;
        /// it does nothing for this pipe read, which is a separate ffmpeg
        /// subprocess's decode/scale/format-convert work). Logs at
        /// LogVerbose, one line per call, so a normal render isn't spammed
        /// unless verbose logging is already on for exactly this kind of
        /// investigation. Remove once the bottleneck is confirmed/ruled out
        /// and, if confirmed, once a real fix (most likely a background
        /// prefetch buffer overlapping decode with compositing, rather than
        /// this synchronous blocking read) replaces this method's current
        /// shape — this timing call stays in that fix's way, not something
        /// worth preserving permanently.
        /// </summary>
        public SKImage NextFrame()
        {
            if (!_exhausted)
            {
                var sw = Stopwatch.StartNew();
                byte[] buffer = new byte[_frameByteSize];
                int totalRead = ReadFully(_stdout, buffer);
                sw.Stop();
                _cumulativeReadTime += sw.Elapsed;

                EditSharpConfig.Logger.LogVerbose(
                    $"SkSourceDecoder: pipe read took {sw.ElapsedMilliseconds}ms this frame " +
                    $"({_cumulativeReadTime.TotalMilliseconds:F0}ms cumulative for this decoder).");

                if (totalRead == _frameByteSize)
                {
                    _lastFrameBytes = buffer;
                }
                else
                {
                    // Partial or zero read — source ended. A partial final
                    // read (rather than a clean zero) is possible if the
                    // source's real frame count didn't land exactly on the
                    // requested fps after conforming; treated the same as
                    // a clean EOF rather than as an error, matching the old
                    // path's tolerant freeze-frame behaviour.
                    _exhausted = true;
                }
            }

            if (_lastFrameBytes == null)
                throw new InvalidOperationException(
                    "SkSourceDecoder produced no frames at all — source may be " +
                    "empty, unreadable, or the initial seek landed past its end.");

            return WrapAsImage(_lastFrameBytes);
        }

        private SKImage WrapAsImage(byte[] pixels)
        {
            var info = new SKImageInfo(_width, _height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

            // Copies into an SKData rather than pinning the caller's buffer
            // directly — NextFrame reuses/overwrites its buffer on the next
            // call (for non-frozen frames), so the SKImage needs its own
            // copy to remain valid after this method returns. Not
            // optimized for zero-copy; flagged as a candidate for later if
            // per-frame allocation cost turns out to matter here too (same
            // shape of concern already flagged for ResizeContent etc.).
            SKData data = SKData.CreateCopy(pixels);
            return SKImage.FromPixels(info, data, _width * 4);
        }

        private static int ReadFully(Stream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read == 0) break; // real EOF
                offset += read;
            }
            return offset;
        }

        /// <summary>
        /// Terminates the decode subprocess. Must be called once this
        /// clip's visible window ends, even if the source hadn't reached
        /// EOF yet (clip shorter than its source) — an ffmpeg process piping
        /// to a pipe nobody is draining anymore will otherwise sit blocked
        /// on a full pipe buffer indefinitely rather than exiting on its own.
        /// </summary>
        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // process already exited between the check and the kill —
                // not an error condition worth surfacing
            }
            finally
            {
                _stdout.Dispose();
                _process.Dispose();
            }
        }
    }
}