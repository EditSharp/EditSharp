using EditSharp.Components.Clips;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Channels
{
    public sealed class AudioChannel : Channel
    {
        //channel-level mix gain
        float _volume = 1f;
        [Editable("Volume", Min = 0, Max = 2, Step = 0.01)]
        public float Volume { get => _volume; set => Transaction.Set(this, ref _volume, value, static (o, v) => o._volume = v); }

        //REWRITE: AudibleClip is gone — AudioClip is the only concrete
        //audible Clip type now (see Clip.cs's "clips are graphs" remarks).
        protected internal override bool IsValidClipType(Clip clip) => clip is AudioClip;
    }
}
