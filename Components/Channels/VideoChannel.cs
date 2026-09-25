using EditSharp.Components.Clips;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Channels
{
    /// <summary>A channel of video clips; each video channel draws over the ones below it.</summary>
    public sealed class VideoChannel : Channel
    {
        ChannelBlendMode _blendMode = ChannelBlendMode.SrcOver;
        /// <summary>How the channel combines with the channels below it.</summary>
        [Editable("Blend mode")]
        public ChannelBlendMode BlendMode { get => _blendMode; set => Transaction.Set(this, ref _blendMode, value, static (o, v) => o._blendMode = v); }

        /// <inheritdoc/>
        protected internal override bool IsValidClipType(Clip clip) => clip is VideoClip;
    }
}
