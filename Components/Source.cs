using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Text;

namespace EditSharp.Components
{
    public class Source
    {
        //the type of the source (video, image, audio)
        public required SourceType Type { get; set; }

        //the directory path to the file OR text content
        public required string Path { get; set; }

        //how long after the beginning of the source file to start using it
        public TimeSpan? Start { get; set; }

        //how long the source should be drawn from
        public TimeSpan? Duration { get; set; }

        public Source Duplicate()
        {
            return new()
            {
                Type = Type,
                Path = Path,
                Start = Start,
                Duration = Duration
            };
        }
    }

    public enum SourceType
    {
        Video,
        Image,
        Audio,
    }
}
