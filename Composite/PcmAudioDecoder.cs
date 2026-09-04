using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EditSharp.Components;
 
namespace EditSharp.Composite
{
    /// <summary>
    /// Decodes a AudioSourceNode's Source directly to an in-memory AudioBuffer,
    /// already fitted to exactly the clip's own timeline Duration (trimmed,
    /// looped, or held, same semantics ClipContentBuilder's old ffmpeg
    /// atrim/apad/stream_loop filter lines used to produce) — this is the
    /// FIRST stage of the new real audio pipeline (decode -> graph-evaluate
    /// -> mix), replacing the old ffmpeg-filtergraph-line approach entirely
    /// for actual sample data. See AudioBuffer.FitToFrames for the trim/
    /// loop/hold mechanics themselves; this class's only job is turning a
    /// Source's own [sourceStart, sourceStart+available) window into raw
    /// float PCM once, via a single one-shot ffmpeg decode.
    ///
    /// One ffmpeg process per clip is a deliberate, simple design — clips
    /// are decoded independently and in parallel-safe isolation (no shared
    /// InputGraph/label-numbering state to coordinate, unlike the old
    /// filtergraph approach), and a clip whose Source is reused elsewhere on
    /// the timeline just decodes it again; nothing here caches across
    /// clips. If decode cost ever matters enough to fix, the natural next
    /// step is a decode cache keyed on (Source.Path, window), not a
    /// restructuring of this class's own contract.
    /// </summary>
    internal static class PcmAudioDecoder
    {
        /// <summary>
        /// Decodes `source`'s audio and fits it to exactly `clipDuration` at
        /// `sampleRate`/`channels`. Throws if the source has no audio stream at
        /// all — the caller (AudioMixer) is expected to have already established
        /// this is a AudioSourceNode whose Source is meant to provide sound.
        /// </summary>
        public static async Task<AudioBuffer> DecodeAsync(
            Source source, TimeSpan clipDuration, int sampleRate, int channels,
            CancellationToken token = default)
        {
            MediaInfo info = await MediaProbe.ProbeAsync(source.Path);
 
            if (!info.HasAudio)
                throw new InvalidOperationException($"Source '{source.Path}' has no audio stream to decode.");
 
            bool hasExplicitRange = source.Start.HasValue || source.Duration.HasValue;
            var (sourceStart, available) = SourceTiming.ResolveRange(source, info.Duration);
 
            bool needsLoop = clipDuration.TotalSeconds > available.TotalSeconds + 0.001;
            bool allowLoop = needsLoop && !hasExplicitRange;
 
            AudioBuffer decoded = available.TotalSeconds <= 0.0005
                ? AudioBuffer.Silence(sampleRate, channels, 0)
                : await DecodeWindowAsync(source.Path, sourceStart, available, sampleRate, channels, token);
 
            int targetFrames = AudioBuffer.FramesForDuration(clipDuration, sampleRate);
            return decoded.FitToFrames(targetFrames, allowLoop);
        }
 
        /// <summary>
        /// One-shot ffmpeg decode of [start, start+length) from `path` to raw
        /// interleaved float32 PCM at the requested sample rate/channel count,
        /// entirely in memory (no temp file — the whole point of this pipeline
        /// stage is to hand back real samples, not another file for a later
        /// stage to re-read).
        /// </summary>
        private static async Task<AudioBuffer> DecodeWindowAsync(
            string path, TimeSpan start, TimeSpan length, int sampleRate, int channels, CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = EditSharpConfig.FfmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
 
            foreach (string arg in new[]
            {
                "-v", "error",
                "-ss", start.TotalSeconds.ToString("F4", CultureInfo.InvariantCulture),
                "-t", length.TotalSeconds.ToString("F4", CultureInfo.InvariantCulture),
                "-i", path,
                "-vn",
                "-ar", sampleRate.ToString(CultureInfo.InvariantCulture),
                "-ac", channels.ToString(CultureInfo.InvariantCulture),
                "-f", "f32le",
                "pipe:1",
            })
            {
                psi.ArgumentList.Add(arg);
            }
 
            using var process = new Process { StartInfo = psi };
            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
 
            process.Start();
            process.BeginErrorReadLine();
 
            using var pcm = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(pcm, token);
            await process.WaitForExitAsync(token);
 
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"ffmpeg exited with code {process.ExitCode} decoding audio from '{path}':\n{stderr}");
 
            byte[] bytes = pcm.ToArray();
 
            //truncate to a whole number of interleaved frames — a partial
            //trailing sample (a handful of stray bytes from an odd decode
            //boundary) would otherwise throw in AudioBuffer's constructor
            int bytesPerFrame = channels * sizeof(float);
            int wholeFrameBytes = bytes.Length - (bytes.Length % bytesPerFrame);
 
            var samples = new float[wholeFrameBytes / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, samples, 0, wholeFrameBytes);
 
            return new AudioBuffer(sampleRate, channels, samples);
        }
    }
}
 