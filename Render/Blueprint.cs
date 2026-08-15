using EditSharp.Components;
using System;
using System.Collections.Generic;
using System.Text;

namespace EditSharp.Render
{
    public class Blueprint
    {
        //timeline to render
        public required Timeline Timeline { get; set; }

        //output resolution of rendered video
        public required (int, int) Resolution { get; set; }

        //output framerate of rendered video
        public required int Framerate { get; set; }

        //what encoding to render video with
        public VideoCodec VideoCodec { get; set; } = VideoCodec.H265;

        //what encoding to render audio with
        public AudioCodec AudioCodec { get; set; } = AudioCodec.AAC;

        //whether to use gpu acceleration and what kind
        public HardwareAccelerator HardwareAccelerator { get; set; } = HardwareAccelerator.None;

        //how many sources can have their optimized media built concurrently
        public int ExtractionConcurrency { get; set; } = 1;

        //how many output frames can be rendered concurrently. Defaults to 1
        //(fully sequential, matching the original behaviour) rather than
        //processor count — each in-flight frame holds a full canvas-sized
        //rgba64le frame in memory (e.g. ~130MB at 1920x1080) until it's this
        //render's turn to flush, so RAM and disk I/O both scale with this
        //directly. Raise it deliberately, watching both, rather than
        //defaulting to something that scales with core count.
        public int FrameRenderConcurrency { get; set; } = 4;

        //path to where output should be rendered
        public required string OutputDirectory { get; set; }
    }
}
