using EditSharp.Components.Clips;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Channels
{
    public sealed class VideoChannel : Channel
    {
        //how this channel combines with everything beneath it
        ChannelBlendMode _blendMode = ChannelBlendMode.SrcOver;
        [Editable("Blend mode")]
        public ChannelBlendMode BlendMode { get => _blendMode; set => Transaction.Set(this, ref _blendMode, value, static (o, v) => o._blendMode = v); }

        //REWRITE: VisualClip is gone — VideoClip is the only concrete
        //visual Clip type now (see Clip.cs's "clips are graphs" remarks).
        protected internal override bool IsValidClipType(Clip clip) => clip is VideoClip;
    }
}
