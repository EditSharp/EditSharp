using SkiaSharp;
using System;
using System.Numerics;

namespace EditSharp.Components.Effects
{
    public class DropShadowEffect : Effect
    {
        public SKColor Color { get; set; } = SKColors.Black;
        public Vector2 Offset { get; set; } = new Vector2(0.02f, -0.02f); // normalized, matches Position
        //normalized against canvas width, same convention as BlurEffect.Radius —
        //NOT a pixel count. 0.005 is roughly a 10px blur at 1920 wide. A literal
        //pixel-ish default here (e.g. 5f) sends gblur a sigma in the thousands
        //and ffmpeg rejects it outright (sigma is capped at 1024)
        public float BlurRadius { get; set; } = 0.005f;
        public float Opacity { get; set; } = 0.5f;

        //PostTransform by default: the shadow needs room around the content to
        //blur and offset into, which only exists once the frame is canvas-sized.
        //PreTransform casts the shadow in the clip's own space so it rotates with
        //the content, but then needs padding built into the clip frame first
        public override EffectStage Stage { get; set; } = EffectStage.PostTransform;

        public override Effect Duplicate() => new DropShadowEffect
        {
            Enabled = Enabled,
            Stage = Stage,
            Color = Color,
            Offset = Offset,
            BlurRadius = BlurRadius,
            Opacity = Opacity,
        };
    }
}
