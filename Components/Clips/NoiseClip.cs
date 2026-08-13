using EditSharp.Components.Clips;
using System;
using System.Collections.Generic;
using System.Text;

namespace EditSharp.Components
{
    public class NoiseClip : Clip
    {
        //seed used to generate perlin noise
        public int Seed { get; set; } = Random.Shared.Next();

        //value between 0 and 1 to determine how detailed the noise should be
        public float Detail { get; set; } = 0.03f;

        //value between 0 and 1 to determine how fast the noise should seethe
        public float SeetheRate { get; set; } = 0.03f;

        public override NoiseClip Duplicate()
        {
            return new()
            {
                Seed = Seed,
                Detail = Detail,
                SeetheRate = SeetheRate,
                Start = Start,
                Duration = Duration,
                Modulate = Modulate,
                Transform = Transform.Duplicate(),
                Keyframes = [.. Keyframes.Select(k => k.Duplicate())],
                Effects = [.. Effects.Select(e => e.Duplicate())],
            };
        }

    }
}
