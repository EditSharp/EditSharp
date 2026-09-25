using EditSharp.Components;
using EditSharp.Rendering;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace EditSharp.Rendering
{
    public class Blueprint
    {
        //timeline to render
        public required Timeline Timeline { get; set; }

        public required RenderSettings RenderSettings { get; set; }

        //path to where output should be rendered
        public required string OutputDirectory { get; set; }
    }
}
