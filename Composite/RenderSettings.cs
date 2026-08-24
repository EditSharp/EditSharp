using System;
using System.Numerics;
 
namespace EditSharp.Composite;
 
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
 
         //whether to use gpu acceleration and what kind
        public HardwareAccelerator HardwareAccelerator { get; set; } = HardwareAccelerator.GPU;
 
        /// <summary>
        /// Which GPU the Skia compositor should run on, as a DXGI adapter
        /// index. NULL (the default) means AUTO: pick the first non-software
        /// adapter, which is what every prior version did implicitly.
        ///
        /// This exists because "which GPU is this actually running on" turned
        /// out to be a load-bearing question rather than an implementation
        /// detail. On a hybrid machine the first non-software adapter may be
        /// the integrated GPU or the discrete one depending on enumeration
        /// order, system power settings, and BIOS mode — so the same build
        /// can silently execute the whole compositor on a different vendor's
        /// driver and shader compiler from one boot to the next. Making the
        /// choice explicit turns that into a setting instead of a surprise,
        /// and gives a direct way to A/B two GPUs with one binary at one
        /// moment when something renders differently than expected.
        ///
        /// Adapter indices are logged at the start of every GPU session (see
        /// GpuContext), so the valid values for a given machine are visible
        /// without guessing. An index that doesn't exist, or names a software
        /// adapter, falls back to software rendering with a logged warning
        /// rather than failing the render.
        ///
        /// Ignored entirely when HardwareAccelerator is None.
        /// </summary>
        public int? GpuAdapterIndex { get; set; } = null;
 
        public RenderSettings() { }
 
    }
 