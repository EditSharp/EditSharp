using EditSharp.Components.Media;
using EditSharp.Components;
using System;
using System.Numerics;
using EditSharp.Video;

namespace EditSharp.Rendering;

/// <summary>How a timeline is rendered: output size, frame rate, codecs and the GPU to use.</summary>
public struct RenderSettings
{
    /// <summary>The output size in pixels.</summary>
    public Vector2 Resolution { get; set; } = new(1920, 1080);

    /// <summary>Output frames per second.</summary>
    public int Framerate { get; set; } = 30;

    /// <summary>The video codec to encode with.</summary>
    public VideoCodec VideoCodec { get; set; } = VideoCodec.H265;

    /// <summary>The audio codec to encode with.</summary>
    public AudioCodec AudioCodec { get; set; } = AudioCodec.AAC;

    /// <summary>Whether compositing and encoding use the GPU, and which kind.</summary>
    public HardwareAccelerator HardwareAccelerator { get; set; } = HardwareAccelerator.GPU;

    /// <summary>The DXGI adapter the compositor runs on; null picks the first hardware adapter.</summary>
    /// <remarks>
    /// On a machine with integrated and discrete GPUs, which one comes first can
    /// change between boots, so set this to pin one. Adapter indices are logged
    /// at the start of every GPU session. An index that doesn't exist, or names a
    /// software adapter, falls back to software rendering with a warning. Ignored
    /// when <see cref="HardwareAccelerator"/> is None.
    /// </remarks>
    public int? GpuAdapterIndex { get; set; } = null;

    /// <summary>Whether sources with proxies read the proxies or the originals.</summary>
    /// <remarks>Defaults to SourceOnly, so an export is full quality; previews opt into ProxiesOnly. Read when a session prepares its sources, so a change applies from the next session.</remarks>
    public SourceMode SourceMode { get; set; } = SourceMode.SourceOnly;

    /// <summary>Settings with every default: 1920x1080 at 30 fps, H.265 and AAC, on the GPU, from original sources.</summary>
    public RenderSettings() { }
}
