using EditSharp.Components.Media;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EditSharp.Audio.Engine;
using EditSharp.Audio.Processors;

namespace EditSharp.Components.Nodes.Input
{
    /// <summary>An input that brings sound into an audio graph, read as a stream.</summary>
    /// <remarks>
    /// Output: Audio. Reading has the same two steps as <see cref="VideoInputNode"/>.
    /// PrepareAsync does the slow work once per session, and the prepared handle
    /// opens readers that fill blocks of interleaved float samples, already in the
    /// format the caller asked for, from a content time on.
    /// </remarks>
    public abstract class AudioInputNode : InputNode
    {
        private static readonly NodePort[] StaticPorts = [new("Audio", PortType.Audio, PortDirection.Output)];

        /// <inheritdoc/>
        public override IReadOnlyList<NodePort> Ports => StaticPorts;

        /// <summary>Readies the input for one session.</summary>
        /// <param name="ct">Cancels preparing.</param>
        /// <returns>The prepared input, whose readers take content time; the caller disposes it.</returns>
        /// <exception cref="SourceUnavailableException">The input can't provide content.</exception>
        internal abstract Task<IPreparedAudioSource> PrepareAsync(CancellationToken ct = default);

        internal override IAudioProcessor CreateAudioProcessor(AudioSession session) =>
            new ContentInputProcessor(() => ContentIdentity, () => new InputContentAudio(this, session), session);
    }
}
