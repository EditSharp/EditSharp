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
        public HardwareAccelerator HardwareAccelerator { get; set; } = HardwareAccelerator.GPU;

        //how many sources can have their optimized media built concurrently
        //
        //STILL USED as of the Skia migration's current state, but flagged:
        //OptimizedMediaBuilder's own reason to exist for VIDEO sources is
        //gone per item 11 (SkSourceDecoder pipes directly, no pre-render) —
        //this property's relevance narrows to whatever OptimizedMediaBuilder
        //work survives that item's full audit, not confirmed removed here.
        public int ExtractionConcurrency { get; set; } = 3;

        // FrameRenderConcurrency REMOVED — decided in conversation (Skia
        // migration item 11): parallel OUTPUT frames are incompatible with
        // SkSourceDecoder's one-ordered-pipe-per-source model, since a pipe
        // has one current read position and two concurrent frame-renderers
        // can't both be "the next reader" of the same source stream. The
        // render is now committed to strictly sequential frame order.
        // Real single-threaded Skia throughput is validated later, at
        // checklist item 14 — this removal is not contingent on that
        // measurement turning out favourably, per the explicit decision to
        // commit now and validate after.

        //how many consecutive output frames are rendered by a SINGLE ffmpeg
        //process. 1 reproduces the original one-process-per-frame behaviour
        //exactly.
        //
        //FLAGGED, NOT REMOVED THIS PASS: this property's whole rationale
        //(amortizing a fixed per-INVOCATION ffmpeg cost across several
        //frames) applies only to the old one-ffmpeg-process-per-frame
        //render model. Once FrameRenderer's core loop is rewired to the
        //Skia in-process compositor (the item-13 orchestration work, not
        //done in this pass), there is no per-frame ffmpeg invocation left
        //to amortize, and this property becomes as dead as
        //FrameRenderConcurrency above. Left in place here because removing
        //it correctly requires that same orchestration rewrite — deleting
        //it in isolation now would just be guessing at what item 13 needs,
        //not fixing an active correctness bug the way FrameRenderConcurrency
        //was. Revisit at item 13, not before.
        public int FrameBatchSize { get; set; } = 4;

        //path to where output should be rendered
        public required string OutputDirectory { get; set; }
    }
}
