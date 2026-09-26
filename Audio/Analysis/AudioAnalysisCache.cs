using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EditSharp.Components.Media;
using EditSharp.History;
using EditSharp.Video;

namespace EditSharp.Audio.Analysis
{
    /// <summary>Keeps every media file's <see cref="AudioAnalysis"/>: on disk beside the proxies, and in memory once loaded.</summary>
    /// <remarks>
    /// Files are keyed by the media hash like proxies and live as .esra files under <see cref="EditSharpConfig.ProxyDirectory"/>.
    /// An analysis is built once, streaming the file through ffmpeg at the analysis rate, and then read from disk on later runs.
    /// </remarks>
    public static class AudioAnalysisCache
    {
        private static readonly ConcurrentDictionary<string, AudioAnalysis> Loaded = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Lazy<Task<AudioAnalysis>>> Building = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Fires when a file's analysis has been built or loaded, with the file's path, on a thread pool thread.</summary>
        public static event Action<string>? Completed;

        /// <summary>The analysis of a file, if it's in memory.</summary>
        /// <param name="path">The media file.</param>
        /// <param name="analysis">The analysis when it's there.</param>
        /// <returns>True when <paramref name="analysis"/> is set. Nothing is started otherwise.</returns>
        public static bool TryGet(string path, out AudioAnalysis analysis)
        {
            analysis = null!;
            return !string.IsNullOrEmpty(path) && Loaded.TryGetValue(Path.GetFullPath(path), out analysis!);
        }

        /// <summary>Whether a file's analysis is being built or loaded right now.</summary>
        /// <param name="path">The media file.</param>
        /// <returns>True while it is.</returns>
        public static bool IsPending(string path) => !string.IsNullOrEmpty(path) && Building.ContainsKey(Path.GetFullPath(path));

        /// <summary>The analysis of a file: from memory, else from disk, else built and saved.</summary>
        /// <remarks>Concurrent callers for one file share one build.</remarks>
        /// <param name="path">The media file.</param>
        /// <param name="ct">Cancels waiting; a build in progress carries on for the others.</param>
        /// <returns>The analysis.</returns>
        /// <exception cref="FileNotFoundException"><paramref name="path"/> doesn't exist.</exception>
        /// <exception cref="SourceUnavailableException">The file can't provide samples.</exception>
        public static async Task<AudioAnalysis> GetAsync(string path, CancellationToken ct = default)
        {
            string full = Path.GetFullPath(path);
            if (Loaded.TryGetValue(full, out AudioAnalysis? known)) return known;
            if (!File.Exists(full)) throw new FileNotFoundException($"Media not found: '{path}'", path);

            Lazy<Task<AudioAnalysis>> build = Building.GetOrAdd(full, _ => new Lazy<Task<AudioAnalysis>>(() => Task.Run(() => BuildOrLoadAsync(full))));

            try
            {
                return await build.Value.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                if (build.Value.IsCompleted) Building.TryRemove(full, out _);
            }
        }

        /// <summary>Drops a file's analysis from memory and disk, so the next request rebuilds it.</summary>
        /// <param name="path">The media file.</param>
        public static void Forget(string path)
        {
            string full = Path.GetFullPath(path);
            Loaded.TryRemove(full, out _);

            try
            {
                string file = FileFor(MediaHasher.ComputeAsync(full).GetAwaiter().GetResult());
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                EditSharpConfig.Logger.LogWarning($"Could not delete the audio analysis of '{path}': {ex.Message}");
            }
        }

        private static async Task<AudioAnalysis> BuildOrLoadAsync(string full)
        {
            string hash = await MediaHasher.ComputeAsync(full).ConfigureAwait(false);
            string file = FileFor(hash);

            AudioAnalysis? analysis = null;

            if (File.Exists(file))
            {
                try
                {
                    using FileStream stream = File.OpenRead(file);
                    analysis = AudioAnalysis.Load(stream);
                }
                catch (Exception ex) when (ex is IOException or EndOfStreamException)
                {
                    EditSharpConfig.Logger.LogWarning($"Could not read the audio analysis '{file}': {ex.Message}");
                }
            }

            if (analysis is null)
            {
                analysis = await BuildAsync(full).ConfigureAwait(false);

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    string temp = file + ".part";
                    using (FileStream stream = File.Create(temp)) analysis.Save(stream);
                    File.Move(temp, file, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    EditSharpConfig.Logger.LogWarning($"Could not save the audio analysis '{file}': {ex.Message}");
                }
            }

            Loaded[full] = analysis;
            Completed?.Invoke(full);
            return analysis;
        }

        private static async Task<AudioAnalysis> BuildAsync(string full)
        {
            AudioMedia media = Transaction.Suppressed(() => new AudioMedia { Path = full });
            using IPreparedAudioSource prepared = await media.PrepareAsync().ConfigureAwait(false);
            using IAudioSampleReader reader = prepared.OpenReader(new AudioReaderOptions(AudioAnalysis.SampleRate, 1, TimeSpan.Zero));
            return AudioAnalysis.Build(reader, null, CancellationToken.None);
        }

        private static string FileFor(string hash) => Path.Combine(EditSharpConfig.ProxyDirectory, hash[..2], hash + ".esra");
    }
}
