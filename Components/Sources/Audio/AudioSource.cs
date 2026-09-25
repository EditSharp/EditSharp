using System.Threading;
using System.Threading.Tasks;

namespace EditSharp.Components.Sources.Audio
{
    /// <summary>A source of sound, read as a stream.</summary>
    /// <remarks>
    /// Reading has the same two steps as <see cref="Video.VideoSource"/>. PrepareAsync
    /// does the slow work once per session, and the prepared handle opens readers
    /// that fill blocks of interleaved float samples, already in the format the
    /// caller asked for.
    /// </remarks>
    public abstract class AudioSource : Source
    {
        /// <summary>Readies the source for one session.</summary>
        /// <param name="ct">Cancels preparing.</param>
        /// <returns>The prepared source; the caller disposes it.</returns>
        /// <exception cref="SourceUnavailableException">The source can't provide content.</exception>
        internal abstract Task<IPreparedAudioSource> PrepareAsync(CancellationToken ct = default);

        /// <inheritdoc/>
        public override AudioSource Duplicate() => (AudioSource)base.Duplicate();
    }
}
