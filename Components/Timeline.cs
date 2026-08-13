using System;
using System.Collections.Generic;
using System.Linq;

namespace EditSharp.Components
{
    public class Timeline
    {
        //channels to be rendered from bottom to top —
        //index 0 composites first, each later channel on top of the result
        public required List<Channel> Channels { get; set; }

        //the timeline runs as long as its longest channel
        public TimeSpan Duration =>
            Channels.Count == 0 ? TimeSpan.Zero : Channels.Max(c => c.End);
    }
}
