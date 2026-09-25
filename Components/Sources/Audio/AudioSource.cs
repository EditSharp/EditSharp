using System.Threading;
using System.Threading.Tasks;

namespace EditSharp.Components.Sources.Audio
{
    /// <summary>
    /// A source of sound, read as a stream: PrepareAsync does the slow
    /// once-per-session work, and the prepared handle opens readers that pull
    /// interleaved float samples block by block, already in the format the
    /// caller asked for. Mirrors VideoSource's two-phase shape.
    /// </summary>
    public abstract class AudioSource : Source
    {
        internal abstract Task<IPreparedAudioSource> PrepareAsync(CancellationToken ct = default);

        public override AudioSource Duplicate() => (AudioSource)base.Duplicate();
    }
}
