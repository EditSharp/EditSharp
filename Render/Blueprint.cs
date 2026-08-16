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
        public int ExtractionConcurrency { get; set; } = 3;

        //how many output frames can be rendered concurrently. Defaults to 1
        //(fully sequential, matching the original behaviour) rather than
        //processor count — each in-flight frame holds a full canvas-sized
        //rgba64le frame in memory (e.g. ~130MB at 1920x1080) until it's this
        //render's turn to flush, so RAM and disk I/O both scale with this
        //directly. Raise it deliberately, watching both, rather than
        //defaulting to something that scales with core count.
        public int FrameRenderConcurrency { get; set; } = 1;

        //how many consecutive output frames are rendered by a SINGLE ffmpeg
        //process. 1 reproduces the original one-process-per-frame behaviour
        //exactly.
        //
        //This exists because a measurable fixed cost is paid per ffmpeg
        //INVOCATION that has nothing to do with how much work the frame
        //itself needs — shared-library loading, codec/format/filter registry
        //init, graph configuration. Measured on a minimal blueprint (one
        //generator clip, ZERO file inputs, a three-line filter graph) at
        //roughly 180ms per frame at 1080p, against a separately-measured
        //process spawn cost of only 3-5ms — i.e. the bulk of it lands inside
        //what a spawn-vs-run timing split attributes to "run", and is
        //invisible to that split. Batching N frames into one process pays
        //that cost once per batch instead of once per frame.
        //
        //RAM COST, which is the reason this is not defaulted high: an entire
        //batch's frames are held in memory until the batch completes and its
        //turn to flush comes up. At 1920x1080 gbrap16le (8 bytes/pixel) one
        //frame is ~16.6MB, so peak usage is roughly
        //    FrameBatchSize x FrameRenderConcurrency x 16.6MB
        //A batch of 8 at concurrency 1 is ~133MB; the same batch at
        //concurrency 4 is ~530MB. Raising BOTH multiplies, and swapping is
        //far slower than any per-invocation cost this saves.
        //
        //Batches also multiply INPUT count: every frame in a batch still
        //opens its own file inputs, so a batch of N over a blueprint with 2
        //file-backed clips opens 2N inputs in one process. That is the part
        //of per-frame cost this does NOT amortize.
        public int FrameBatchSize { get; set; } = 4;

        //path to where output should be rendered
        public required string OutputDirectory { get; set; }
    }
}
