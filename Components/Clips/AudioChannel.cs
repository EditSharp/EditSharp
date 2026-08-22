using EditSharp.Components.Clips;
 
namespace EditSharp.Components
{
    public sealed class AudioChannel : Channel
    {
        //channel-level mix gain
        public float Volume { get; set; } = 1f;
 
        //REWRITE: AudibleClip is gone — AudioClip is the only concrete
        //audible Clip type now (see Clip.cs's "clips are graphs" remarks).
        protected internal override bool IsValidClipType(Clip clip) => clip is AudioClip;
    }
}
 