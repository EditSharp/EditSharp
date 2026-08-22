using EditSharp.Components.Clips;
 
namespace EditSharp.Components
{
    public sealed class VideoChannel : Channel
    {
        //how this channel combines with everything beneath it
        public ChannelBlendMode BlendMode { get; set; } = ChannelBlendMode.SrcOver;
 
        //REWRITE: VisualClip is gone — VideoClip is the only concrete
        //visual Clip type now (see Clip.cs's "clips are graphs" remarks).
        protected internal override bool IsValidClipType(Clip clip) => clip is VideoClip;
    }
}
 