using EditSharp.Components;
using EditSharp.Rendering;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace EditSharp.Rendering
{
    /// <summary>Everything a render needs: what to render, how, and where the file goes.</summary>
    public class Blueprint
    {
        /// <summary>The timeline to render.</summary>
        public required Timeline Timeline { get; set; }

        /// <summary>How to render it.</summary>
        public required RenderSettings RenderSettings { get; set; }

        /// <summary>The full path of the output file, including its extension; the extension picks the container.</summary>
        public required string OutputPath { get; set; }
    }
}
