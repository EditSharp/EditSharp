using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;

namespace EditSharp.Composite
{
    /// <summary>
    /// Where a source file's I-frames actually are — the shared foundation
    /// under both scrubbing and reverse playback (see ScrubFrameSource and
    /// Playback's "I-FRAME-ONLY SCRUB/REVERSE" remarks).
    ///
    /// WHY AN EXPLICIT INDEX, RATHER THAN JUST LETTING ffmpeg's OWN `-ss`
    /// (BEFORE `-i`) FAST-SEEK DO THE WORK: ffmpeg's fast seek already snaps
    /// to the nearest preceding keyframe on its own, so in principle no index
    /// is needed at all to land on SOME keyframe quickly. What it doesn't
    /// give a caller is which keyframe it actually landed on — the exact
    /// timestamp is needed for two things a fuzzy seek can't provide:
    /// reporting an accurate scrub/reverse position back to the caller, and
    /// stepping deliberately from keyframe to keyframe (reverse playback,
    /// repeated scrubbing) without re-discovering the same seek by trial and
    /// error every time. Building the index once per source and reusing it
    /// turns every later scrub/reverse tick into an O(log n) lookup plus one
    /// exact-timestamp seek, instead of a guess.
    ///
    /// ONE ffprobe SCAN, CACHED FOR THE PROCESS'S LIFETIME, keyed by source
    /// path. `-skip_frame nokey` tells ffprobe's own decoder to skip
    /// decoding every non-keyframe outright rather than decode-then-discard
    /// them, which is what actually keeps this scan cheap on a long source —
    /// it's a demux-and-skip pass, not a full decode.
    /// </summary>
    internal static class KeyframeIndex
    {
        private static readonly ConcurrentDictionary<string, Task<IReadOnlyList<double>>> Cache = new();

        public static Task<IReadOnlyList<double>> GetAsync(string sourcePath) =>
            Cache.GetOrAdd(sourcePath, ProbeAsync);

        private static async Task<IReadOnlyList<double>> ProbeAsync(string sourcePath)
        {
            var args = new List<string>
            {
                "-v", "error",
                "-select_streams", "v:0",
                "-skip_frame", "nokey",
                "-show_entries", "frame=pts_time",
                "-of", "csv=print_section=0",
                sourcePath,
            };

            var psi = new ProcessStartInfo
            {
                FileName = EditSharpConfig.FfprobePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"ffprobe keyframe scan of '{sourcePath}' exited with code {process.ExitCode}:{Environment.NewLine}{stderr}");

            var timestamps = new List<double>();
            foreach (string rawLine in stdout.ToString().Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                if (double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                    timestamps.Add(seconds);
            }

            timestamps.Sort();

            // A source ffprobe couldn't find any keyframe entry for at all
            // (degenerate/very short/unusual container) — treat t=0 as the
            // only available anchor rather than leaving callers with an
            // empty index to divide-by-zero-shaped bugs on.
            if (timestamps.Count == 0) timestamps.Add(0);

            return timestamps;
        }

        /// <summary>
        /// The latest keyframe at or before `targetSeconds` — the seek target
        /// for "show me approximately this position, instantly." Falls back
        /// to the very first keyframe when `targetSeconds` precedes every
        /// keyframe (a source whose first keyframe isn't at exactly t=0 does
        /// happen, and this is the closest available position rather than an
        /// error).
        /// </summary>
        public static double FindAtOrBefore(IReadOnlyList<double> keyframes, double targetSeconds)
        {
            int index = UpperBound(keyframes, targetSeconds) - 1;
            if (index < 0) index = 0;
            return keyframes[index];
        }

        /// <summary>
        /// The nearest keyframe strictly before `beforeSeconds` — used to
        /// step one position further back (reverse playback ticking, or
        /// scrubbing further left than the current keyframe). Null once
        /// `beforeSeconds` is at or before the very first keyframe — nowhere
        /// further back to go.
        /// </summary>
        public static double? FindStrictlyBefore(IReadOnlyList<double> keyframes, double beforeSeconds)
        {
            int index = LowerBound(keyframes, beforeSeconds) - 1;
            return index < 0 ? null : keyframes[index];
        }

        /// <summary>First index whose value is &gt;= `value`.</summary>
        private static int LowerBound(IReadOnlyList<double> sorted, double value)
        {
            int lo = 0, hi = sorted.Count;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (sorted[mid] < value) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        /// <summary>First index whose value is &gt; `value`.</summary>
        private static int UpperBound(IReadOnlyList<double> sorted, double value)
        {
            int lo = 0, hi = sorted.Count;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (sorted[mid] <= value) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }
    }
}