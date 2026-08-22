using System;
using SkiaSharp;
using Vortice.Direct3D12;
using Vortice.DXGI;
 
namespace EditSharp.Composite
{
    /// <summary>
    /// Owns the GRContext the Skia compositor's surfaces are backed by for
    /// one render session (see FrameRenderer.RenderCoreAsync, which creates
    /// exactly one of these and one SkSurfacePool on top of it, both scoped
    /// to that single render).
    ///
    /// PRIORITY, decided in conversation: D3D12 first (fastest, and headless
    /// device creation needs no window/HWND at all — a clean fit for this
    /// fully-headless render process), then ANGLE/GL, then software.
    ///
    /// D3D12 PATH IS A REAL ATTEMPT, NOT A STUB — but genuinely UNVERIFIED,
    /// same honesty flag as SkNoiseClip's SKRuntimeEffect surface (item 10,
    /// #17 in the deferred list) and for the identical reason: no SkiaSharp/
    /// Vortice toolchain exists in this sandbox to build-test against. Two
    /// specific risk points, named rather than hidden:
    ///   1. SkiaSharp's GRD3DBackendContext field names/shape (Adapter/
    ///      Device/Queue/MemoryAllocator/ProtectedContext) are written from
    ///      general familiarity with SkiaSharp's D3D backend, not confirmed
    ///      against whatever version is actually referenced in this project.
    ///   2. This adds TWO NEW PACKAGE DEPENDENCIES not otherwise used
    ///      anywhere in EditSharp: Vortice.Direct3D12 and Vortice.DXGI —
    ///      chosen over hand-written COM/vtable P/Invoke (which would be
    ///      even higher-risk to get right unverified) and over the
    ///      deprecated/archived SharpDX. Add both via NuGet before this will
    ///      even compile.
    /// If either the field names or the Vortice call shapes are wrong, this
    /// should fail LOUDLY at the TryCreateD3D12 try/catch below (falls
    /// through to ANGLE/GL, then software, with a logged reason) rather than
    /// silently producing a broken context — but a compile error is still
    /// the likely first symptom, same as the earlier SKRuntimeEffect.Create
    /// mismatch. Expect to need to fix this by hand against the real
    /// installed package versions.
    ///
    /// ANGLE/GL PATH DELIBERATELY SCOPED DOWN THIS PASS, not silently
    /// skipped: headless EGL context creation is raw P/Invoke against
    /// libEGL.dll with real risk of getting an EGL attribute list or enum
    /// value wrong with no way to verify it here, and D3D12 already covers
    /// the actual target machine (Windows + RTX 5070) — so rather than
    /// shipping a second equally-unverified interop surface in the same
    /// pass, this stays a logged not-yet-implemented fallback for now.
    /// Revisit if a real need shows up (a machine where D3D12 device
    /// creation itself fails but GL/ANGLE would have worked).
    /// </summary>
    internal sealed class GpuContext : IDisposable
    {
        public GRContext? GRContext { get; }
 
        private readonly IDXGIFactory4? _factory;
        private readonly ID3D12Device? _device;
        private readonly ID3D12CommandQueue? _queue;
 
        private GpuContext(GRContext? grContext, IDXGIFactory4? factory, ID3D12Device? device, ID3D12CommandQueue? queue)
        {
            GRContext = grContext;
            _factory = factory;
            _device = device;
            _queue = queue;
        }
 
        public static GpuContext Create(HardwareAccelerator hwAccel)
        {
            if (hwAccel != HardwareAccelerator.GPU)
                return new GpuContext(null, null, null, null);
 
            GpuContext? d3d12 = TryCreateD3D12();
            if (d3d12 != null)
            {
                EditSharpConfig.Logger.LogVerbose("Composite: using D3D12 GRContext.");
                return d3d12;
            }
 
            // ANGLE/GL scoped out this pass — see class remarks. Falls
            // straight through to software from here.
            EditSharpConfig.Logger.LogWarning(
                "HardwareAccelerator.GPU requested, but D3D12 GRContext creation failed " +
                "and the ANGLE/GL fallback is not implemented yet — composite stage is " +
                "running software raster. Decode/encode hardware acceleration are " +
                "unaffected by this.");
 
            return new GpuContext(null, null, null, null);
        }
 
        private static GpuContext? TryCreateD3D12()
        {
            IDXGIFactory4? factory = null;
            ID3D12Device? device = null;
            ID3D12CommandQueue? queue = null;
 
            try
            {
                factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
 
                for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success; i++)
                {
                    if (adapter == null) continue;
 
                    // Skip the software (WARP) adapter — GPU was explicitly
                    // requested, and WARP would defeat the entire point
                    // while still "succeeding" at device creation.
                    if ((adapter.Description1.Flags & AdapterFlags.Software) != 0)
                    {
                        adapter.Dispose();
                        continue;
                    }
 
                    if (D3D12.D3D12CreateDevice(adapter, Vortice.Direct3D.FeatureLevel.Level_11_0, out device).Success
                        && device != null)
                    {
                        var queueDesc = new CommandQueueDescription(CommandListType.Direct);
                        queue = device.CreateCommandQueue(queueDesc);
 
                        var backendContext = new GRD3DBackendContext
                        {
                            Adapter = adapter.NativePointer,
                            Device = device.NativePointer,
                            Queue = queue.NativePointer,
                        };
 
                        GRContext? grContext = GRContext.CreateDirect3D(backendContext);
                        if (grContext != null)
                            return new GpuContext(grContext, factory, device, queue);
 
                        // Context creation itself failed despite a working
                        // device/queue — clean up this attempt and report
                        // failure to the caller, which logs and falls back.
                        queue.Dispose();
                        device.Dispose();
                        queue = null;
                        device = null;
                    }
 
                    adapter.Dispose();
                }
 
                return null;
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogVerbose($"Composite: D3D12 GRContext creation threw: {ex.Message}");
                queue?.Dispose();
                device?.Dispose();
                factory?.Dispose();
                return null;
            }
        }
 
        public void Dispose()
        {
            GRContext?.Dispose();
            _queue?.Dispose();
            _device?.Dispose();
            _factory?.Dispose();
        }
    }
}
 