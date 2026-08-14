using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace EditSharp.Render
{
    /// <summary>
    /// Accumulates ffmpeg inputs and filter_complex lines while the timeline is
    /// being walked, and hands out unique filter labels.
    ///
    /// AddInput is lock-guarded because the index it returns has to stay correctly
    /// ordered — it must match ffmpeg's own -i numbering later — and NextLabel is
    /// Interlocked.
    /// </summary>
    internal class InputGraph
    {
        private readonly Lock _inputLock = new();
        private int _label;

        public readonly List<(string Path, bool VerifyExists, string[]? ExtraArgs, bool HardwareDecodable)>
            Inputs = [];
        public readonly FilterLineList FilterLines = [];

        public string NextLabel(string prefix) => $"{prefix}{Interlocked.Increment(ref _label) - 1}";

        /// <summary>
        /// Registers an ffmpeg input and returns its -i index.
        ///
        /// hardwareDecodable states a FACT about the input rather than a request:
        /// true only for a real encoded video file, whose decode a GPU could
        /// plausibly take over. It stays false for the rasterized PNGs this
        /// pipeline feeds in (text, rounded-corner masks) and for audio-only
        /// sources. Whether anything is actually done with it is FfmpegRunner's
        /// decision, made once from the Blueprint — asking for hardware decode on
        /// a PNG is not a no-op but an error: ffmpeg tries to set up the device
        /// for codec png and fails the run outright ("device type cuda needed for
        /// codec png"), verified directly.
        /// </summary>
        public int AddInput(
            string path, bool verifyExists = true, string[]? extraArgs = null,
            bool hardwareDecodable = false)
        {
            lock (_inputLock)
            {
                Inputs.Add((path, verifyExists, extraArgs, hardwareDecodable));
                return Inputs.Count - 1;
            }
        }
    }

    /// <summary>
    /// An ORDERED, thread-safe collection of filter_complex lines.
    ///
    /// This used to be a ConcurrentBag, on the reasoning that filter_complex
    /// resolves its graph by label rather than by the order the chains happen to
    /// appear in. That is true of the graph's TOPOLOGY but not of its format
    /// negotiation, and the difference is not academic: a `split` whose two
    /// branches want different pixel formats — one taking alphaextract, the other
    /// converting to gbrp — negotiates to an alpha-less format when the lines
    /// arrive scrambled, and alphaextract then fails to configure with "Requested
    /// planes not available". The identical graph emitted in dependency order
    /// binds without complaint. Confirmed by running both orderings of the same
    /// graph.
    ///
    /// So lines are kept in insertion order, and the pipeline is built so that
    /// insertion order IS dependency order: a filter is only ever added after the
    /// filters producing its inputs.
    /// </summary>
    internal sealed class FilterLineList : IEnumerable<string>
    {
        private readonly Lock _lock = new();
        private readonly List<string> _lines = [];

        public void Add(string line)
        {
            lock (_lock) _lines.Add(line);
        }

        public int Count
        {
            get { lock (_lock) return _lines.Count; }
        }

        public IEnumerator<string> GetEnumerator()
        {
            //snapshot under the lock so enumeration can't tear if anything is
            //still appending
            List<string> snapshot;
            lock (_lock) snapshot = [.. _lines];

            return snapshot.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
