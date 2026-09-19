using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Text;
using EditSharp.History;
using EditSharp.Editing;
 
namespace EditSharp.Components
{
    public class Source
    {
        //the type of the source (video, image, audio)
        SourceType _type;
        [Editable("Type")]
        public required SourceType Type { get => _type; set => Transaction.Set(this, ref _type, value, static (o, v) => o._type = v); }
 
        //the directory path to the file OR text content
        string _path = null!;
        [Editable("File", Editor = PropertyEditor.Path)]
        public required string Path { get => _path; set => Transaction.Set(this, ref _path, value, static (o, v) => o._path = v); }
 
        //how long after the beginning of the source file to start using it
        TimeSpan? _start;
        [Editable("In point")]
        public TimeSpan? Start { get => _start; set => Transaction.Set(this, ref _start, value, static (o, v) => o._start = v); }
 
        //how long the source should be drawn from
        TimeSpan? _duration;
        [Editable("Duration")]
        public TimeSpan? Duration { get => _duration; set => Transaction.Set(this, ref _duration, value, static (o, v) => o._duration = v); }
 
        public Source Duplicate() => Transaction.Suppressed(() => new Source
        {
            Type = Type,
            Path = Path,
            Start = Start,
            Duration = Duration
        });
    }
 
    public enum SourceType
    {
        Video,
        Image,
        Audio,
    }
}
 