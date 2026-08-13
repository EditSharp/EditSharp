using System;

namespace EditSharp.Components.Effects
{
    public class RoundedCornersEffect : Effect
    {
        // 0 = square corners, 1 = fully rounded (pill/circle)
        public float Radius { get; set; } = 0.1f;

        //PreTransform is both the cheap path and the intuitive one: the content is
        //still an upright rectangle, so the mask is a plain rounded rect that then
        //rotates/warps along with the content. PostTransform has to round the
        //corners of the already-warped quad in screen space, which needs a mask
        //rasterized from the transform's actual output corners
        public override EffectStage Stage { get; set; } = EffectStage.PreTransform;

        public override Effect Duplicate() => new RoundedCornersEffect
        {
            Enabled = Enabled,
            Stage = Stage,
            Radius = Radius,
        };
    }
}
