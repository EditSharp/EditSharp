using EditSharp.Components;
using EditSharp.Render;
using System;
using System.Collections.Generic;
using System.Text;

namespace EditSharp
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
        public HardwareAccelerator HardwareAccelerator { get; set; } = HardwareAccelerator.Nvenc;

        //whether to decode media on the gpu and how
        public HardwareDecoder HardwareDecoder { get; set; } = HardwareDecoder.Cuda;

        //path to where output should be rendered
        public required string OutputDirectory { get; set; }
    }
}
