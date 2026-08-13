using EditSharp.Components.Clips;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace EditSharp.Components
{
    public class GeneratorClip : Clip
    {
        //(color the clip starts as before transitioning to the ColorMain color, transition time)
        //null means the clip begins at ColorMain
        public (SKColor, TimeSpan)? ColorIn { get; set; }

        //color the clip remains as
        public SKColor ColorMain { get; set; }

        //(color the clip ends as by transitioning out of the ColorMain color, transition time)
        //null means the clip ends at ColorMain
        public (SKColor, TimeSpan)? ColorOut { get; set; }

        public override GeneratorClip Duplicate()
        {
            return new()
            {
                ColorIn = ColorIn,
                ColorMain = ColorMain,
                ColorOut = ColorOut,
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
