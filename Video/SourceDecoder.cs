using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using EditSharp.Components;

namespace EditSharp.Video
{
    /// <summary>One ffmpeg process decoding a source forward, conformed to a frame rate, as raw RGBA frames through a pipe.</summary>
    /// <remarks>
    /// The stream is resampled to the reader's fps by ffmpeg, so each frame read
    /// from the pipe is the next frame in order: there's one seek, when the
    /// process starts. When the source runs out, the last frame repeats.
    /// Dispose kills the process and waits for it to exit, because a GPU decode
    /// session's teardown can take long enough to collide with new GPU work
    /// started straight after. <see cref="DecodeSingleFrameAsync"/> decodes one
    /// frame at any position with its own short-lived process.
    /// </remarks>
    internal sealed class SourceDecoder : IDisposable
    {
        //how long Dispose waits for a killed process to exit; running out is logged, not thrown
        private static readonly Time DisposeWaitForExitTimeout = Time.FromSeconds(10);

        private readonly Process _process;
        private readonly Stream _stdout;
        private readonly int _width;
        private readonly int _height;
        private readonly int _frameByteSize;
        private readonly string _sourcePath;
        private readonly IReadOnlyList<string> _ffmpegArgs;
        private byte[]? _lastFrameBytes;
        private bool _exhausted;

        //true once the source has run out; every NextFrame from then on repeats the last frame
        public bool IsExhausted => _exhausted;

        //the last of ffmpeg's stderr, capped since the process can run for minutes; it explains
        //a decode that produced no frames
        private const int MaxStderrCharsCaptured = 4096;
        private readonly StringBuilder _stderrTail = new();
        private readonly object _stderrLock = new();
        private bool _stderrTruncated;

        private SourceDecoder(
            Process process, Stream stdout, int width, int height,
            string sourcePath, IReadOnlyList<string> ffmpegArgs)
        {
            _process = process;
            _stdout = stdout;
            _width = width;
            _height = height;
            _frameByteSize = width * height * 4; // rgba8888
            _sourcePath = sourcePath;
            _ffmpegArgs = ffmpegArgs;

            //keep ffmpeg's stderr (capped) so a decode that yields nothing can say why
            _process.ErrorDataReceived += OnErrorDataReceived;
            _process.BeginErrorReadLine();
        }

        private void OnErrorDataReceived(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;

            lock (_stderrLock)
            {
                if (_stderrTruncated) return;

                if (_stderrTail.Length >= MaxStderrCharsCaptured)
                {
                    _stderrTail.AppendLine("... (further ffmpeg stderr output truncated)");
                    _stderrTruncated = true;
                    return;
                }

                _stderrTail.AppendLine(e.Data);
            }
        }

        //starts decoding from `sourceStart`, conformed to `fps`, at `width` x `height` as rgba8888.
        //`plan` picks hardware decode and GPU or CPU scaling; `fastOpen` skips most of ffmpeg's stream
        //probing and is only for files EditSharp built itself
        public static SourceDecoder Start(
            string sourcePath, Time sourceStart, Rational fps, int width, int height,
            DecodeHwAccelPlan? plan = null, bool fastOpen = false, Rational? speed = null)
        {
            plan ??= DecodeHwAccelPlan.Software;
            string filter = plan.BuildFilterGraph(fps, width, height, speed);

            var args = new List<string>
            {
                "-y", "-v", "error",
            };

            args.AddRange(FfmpegArgs.FilterThreadingArgs());
            args.AddRange(plan.HwAccelArgs);

            if (fastOpen)
            {
                //32 KiB reaches the front-loaded moov atom of a file EditSharp wrote; analyzeduration 0 skips
                //reading stream time to cross-check it
                args.Add("-probesize");
                args.Add("32k");
                args.Add("-analyzeduration");
                args.Add("0");
            }

            if (sourceStart > Time.Zero)
            {
                args.Add("-ss");
                args.Add(FfmpegArgs.Sec(sourceStart));
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

            //spawn time alone, apart from the first read
            EditSharpConfig.Logger.LogVerbose(
                $"SourceDecoder.Start('{sourcePath}', fastOpen={fastOpen}): process spawned in " +
                $"{sw.ElapsedMilliseconds}ms (stream probing, seek and first frame not included; " +
                "see NextFrame's own timing for that).");

            //kept for the no-frames error message
            return new SourceDecoder(
                process, process.StandardOutput.BaseStream, width, height, sourcePath, args);
        }

        //one frame at `seek` from its own ffmpeg process, seeking before -i; cancelling kills the process
        public static async Task<SKImage> DecodeSingleFrameAsync(
            string sourcePath, Time seek, int width, int height,
            DecodeHwAccelPlan? plan = null, CancellationToken ct = default)
        {
            plan ??= DecodeHwAccelPlan.Software;

            //no fps stage for one frame; the GPU scale and download match DecodeHwAccelPlan.BuildFilterGraph
            string scale = plan.UsesGpuScale
                ? $"{plan.ScaleFilterName}={width}:{height}"
                : $"scale={width}:{height}";
            string download = plan.UsesGpuScale ? ",hwdownload,format=nv12" : "";
            string filter = $"{scale}{download},format=rgba";

            var args = new List<string> { "-y", "-v", "error" };
            args.AddRange(FfmpegArgs.FilterThreadingArgs());
            args.AddRange(plan.HwAccelArgs);

            //no fast-open probing limits: the source may not be a file EditSharp wrote

            if (seek > Time.Zero)
            {
                args.Add("-ss");
                args.Add(FfmpegArgs.Sec(seek));
            }

            args.AddRange(new[]
            {
                "-i", sourcePath,
                "-filter:v", filter,
                "-frames:v", "1",
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

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginErrorReadLine();

            //cancelling kills the process; passing it as state avoids allocating when nothing cancels
            using CancellationTokenRegistration killRegistration = ct.Register(static state =>
            {
                var p = (Process)state!;
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* already exited */ }
            }, process);

            int frameByteSize = width * height * 4;
            byte[] buffer = new byte[frameByteSize];
            int totalRead = await ReadFullyAsync(process.StandardOutput.BaseStream, buffer, ct);

            await process.WaitForExitAsync(ct);

            if (totalRead != frameByteSize)
                throw new InvalidOperationException(
                    $"SourceDecoder.DecodeSingleFrameAsync produced no frame for '{sourcePath}' at " +
                    $"{seek} (read {totalRead}/{frameByteSize} bytes, ffmpeg exit " +
                    $"{process.ExitCode}). ffmpeg stderr:{Environment.NewLine}{stderr}");

            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            SKData data = SKData.CreateCopy(buffer);
            return SKImage.FromPixels(info, data, width * 4);
        }

        private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken ct = default)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
                if (read == 0) break; // real EOF
                offset += read;
            }
            return offset;
        }

        //the next frame, in order: call once per output frame. Past the source's end the last frame repeats
        //total time blocked on the pipe, logged with each read
        private Time _cumulativeReadTime = Time.Zero;

        public SKImage NextFrame()
        {
            if (!_exhausted)
            {
                var sw = Stopwatch.StartNew();
                byte[] buffer = new byte[_frameByteSize];
                int totalRead = ReadFully(_stdout, buffer);
                sw.Stop();
                _cumulativeReadTime += Time.FromTimeSpan(sw.Elapsed);

                EditSharpConfig.Logger.LogVerbose(
                    $"SourceDecoder: pipe read took {sw.ElapsedMilliseconds}ms this frame " +
                    $"({_cumulativeReadTime.Milliseconds:F0}ms cumulative for this decoder).");

                if (totalRead == _frameByteSize)
                {
                    _lastFrameBytes = buffer;
                }
                else
                {
                    //a short or empty read is the end: the conformed stream need not end on a whole frame
                    _exhausted = true;
                }
            }

            if (_lastFrameBytes == null)
                throw new InvalidOperationException(BuildNoFramesMessage());

            return WrapAsImage(_lastFrameBytes);
        }

        //the no-frames message: the source, the ffmpeg command, whether it exited and how, and its stderr
        private string BuildNoFramesMessage()
        {
            string processState;
            try
            {
                processState = _process.HasExited
                    ? $"ffmpeg exited with code {_process.ExitCode}"
                    : "ffmpeg is still running (the pipe produced zero bytes without the process exiting; " +
                      "likely blocked on something other than a clean EOF)";
            }
            catch (InvalidOperationException)
            {
                //HasExited and ExitCode can throw while the process tears down
                processState = "ffmpeg process state unavailable";
            }

            string stderrText;
            lock (_stderrLock)
            {
                stderrText = _stderrTail.Length > 0
                    ? _stderrTail.ToString().TrimEnd()
                    : "(no stderr output captured; ffmpeg logged nothing at -v error before this point)";
            }

            var message = new StringBuilder();
            message.AppendLine(
                "SourceDecoder produced no frames at all: the source may be empty, unreadable, or " +
                "the initial seek landed past its end.");
            message.AppendLine($"  Source path: '{_sourcePath}'");
            message.AppendLine($"  Decode target: {_width}x{_height} rgba8888");
            message.AppendLine($"  Process state: {processState}");
            message.AppendLine($"  ffmpeg args: {string.Join(' ', _ffmpegArgs)}");
            message.AppendLine("  ffmpeg stderr:");
            foreach (string line in stderrText.Split('\n'))
                message.AppendLine($"    {line.TrimEnd('\r')}");

            return message.ToString().TrimEnd();
        }

        private SKImage WrapAsImage(byte[] pixels)
        {
            var info = new SKImageInfo(_width, _height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

            //copied, since NextFrame reuses its buffer on the next call
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

        //kills ffmpeg and waits (up to DisposeWaitForExitTimeout) for it to exit. Call when the clip's
        //window ends even if the source hasn't: a process writing to an undrained pipe never exits
        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);

                    if (!_process.WaitForExit(DisposeWaitForExitTimeout.ToTimeSpan()))
                    {
                        EditSharpConfig.Logger.Log(
                            $"SourceDecoder.Dispose('{_sourcePath}'): killed ffmpeg process did not exit " +
                            $"within {DisposeWaitForExitTimeout.Seconds:F0}s; proceeding anyway. If this " +
                            "recurs, a caller doing GPU work immediately after disposing a decoder may still " +
                            "race this process's own teardown.");
                    }
                }
            }
            catch (InvalidOperationException)
            {
                //already exited between the check and the kill
            }
            finally
            {
                _process.ErrorDataReceived -= OnErrorDataReceived;
                _stdout.Dispose();
                _process.Dispose();
            }
        }
    }
}