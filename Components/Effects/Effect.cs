using System;

namespace EditSharp.Components.Effects
{
    public abstract class Effect
    {
        public bool Enabled { get; set; } = true;

        //where in the pipeline this effect runs
        public abstract EffectStage Stage { get; set; }

        //deep copy — clip fragments produced by a split must not share Effect
        //instances, or retuning one fragment's blur silently retunes the other's
        public abstract Effect Duplicate();
    }

    public enum EffectStage
    {
        //clip-local space, before the transform is applied.
        //content is at its own native size and orientation here, so shape-based
        //effects are working with a plain upright rectangle
        PreTransform,

        //canvas space, after the transform is applied.
        //content is positioned/rotated/warped and the frame is canvas-sized, so
        //there is room around it for an effect to bleed into
        PostTransform,
    }
}
