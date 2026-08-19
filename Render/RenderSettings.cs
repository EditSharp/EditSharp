using System;
using System.Numerics;

namespace EditSharp.Render;

public struct RenderSettings
    {
        //output resolution of rendered video
        public Vector2 Resolution { get; set; } = new(1920, 1080);

        //output framerate of rendered video
        public int Framerate { get; set; } = 30;

        //what encoding to render video with
        public VideoCodec VideoCodec { get; set; } = VideoCodec.H265;

        //what encoding to render audio with
        public AudioCodec AudioCodec { get; set; } = AudioCodec.AAC;

        public RenderSettings() { }

    }
