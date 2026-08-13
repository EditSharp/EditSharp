using EditSharp.Components.Clips;
using System;
using System.Linq;

namespace EditSharp.Components
{
    public class SourceClip : Clip
    {
        //base source
        public required Source Source { get; set; }

        //if the source is audio or video, how loud the audio should be
        public float Volume { get; set; } = 1f;

        //trimming the clip's head means the material under the removed section is
        //gone too — without advancing the in-point, the clip would simply replay
        //what was just cut off
        protected override void OnTrimmedFromStart(TimeSpan amount)
        {
            Source.Start = (Source.Start ?? TimeSpan.Zero) + amount;
        }

        public override SourceClip Duplicate()
        {
            return new()
            {
                Source = Source.Duplicate(),
                Volume = Volume,
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
