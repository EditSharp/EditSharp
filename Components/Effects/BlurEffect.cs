using System;

namespace EditSharp.Components.Effects
{
    public class BlurEffect : Effect
    {
        // Gaussian blur strength
        public float Radius { get; set; } = 0.01f;

        //PreTransform blurs in the clip's own space, so the blur scales with the
        //clip as it is scaled; PostTransform blurs in screen space, so it stays a
        //constant on-screen size no matter how the clip is transformed
        public override EffectStage Stage { get; set; } = EffectStage.PreTransform;

        public override Effect Duplicate() => new BlurEffect
        {
            Enabled = Enabled,
            Stage = Stage,
            Radius = Radius,
        };
    }
}
