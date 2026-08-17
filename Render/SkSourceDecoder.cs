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
        /// seek cost this replaces), conformed to `fps`, at `width`x`height`.
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
        /// <summary>
        /// `hwAccelArgs` — e.g. ["-hwaccel", "cuda"] or empty for software —
        /// resolved once up front by FfmpegRunner.GetDecodeHwAccelArgsAsync
        /// against this exact source path (see FrameRenderer.ProbeVideoAsync,
        /// where that probe now runs alongside MediaProbe) and passed straight
        /// through here, not re-resolved per clip/frame. Must appear before
        /// `-i` — ffmpeg's -hwaccel is an input-scoped option.
        /// </summary>
        public static SkSourceDecoder Start(
            string sourcePath, double sourceStartSeconds, int fps, int width, int height,
            System.Collections.Generic.IReadOnlyList<string>? hwAccelArgs = null)
        {
            string filter = $"fps={fps},scale={width}:{height}," +
                             $"format=rgba,settb=AVTB";

            var args = new System.Collections.Generic.List<string>
            {
                "-y", "-v", "error",
            };

            args.AddRange(GraphUtilities.FilterThreadingArgs());

            if (hwAccelArgs != null && hwAccelArgs.Count > 0)
                args.AddRange(hwAccelArgs);

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

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Start();

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
        public SKImage NextFrame()
        {
            if (!_exhausted)
            {
                byte[] buffer = new byte[_frameByteSize];
                int totalRead = ReadFully(_stdout, buffer);

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
