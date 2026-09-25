using EditSharp.Components.Clips;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Channels
{
    /// <summary>A channel of audio clips; every audio channel is mixed together.</summary>
    public sealed class AudioChannel : Channel
    {
        float _volume = 1f;
        /// <summary>The channel's volume as a multiplier: 0 is silent, 1 unchanged.</summary>
        [Editable("Volume", Min = 0, Max = 2, Step = 0.01)]
        public float Volume { get => _volume; set => Transaction.Set(this, ref _volume, value, static (o, v) => o._volume = v); }

        /// <inheritdoc/>
        protected internal override bool IsValidClipType(Clip clip) => clip is AudioClip;
    }
}
