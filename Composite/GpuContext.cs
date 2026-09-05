using System;
using System.Runtime.InteropServices;
using SkiaSharp;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace EditSharp.Composite
{
    /// <summary>
    /// Owns the GRContext the Skia compositor's surfaces are backed by for
    /// one render session (see Renderer.RenderCoreAsync, which creates
    /// exactly one of these and one SkSurfacePool on top of it, both scoped
    /// to that single render).
    ///
    /// PRIORITY: D3D12 first (fastest, and headless device creation needs no
    /// window/HWND at all — a clean fit for this fully-headless render
    /// process), then software. The ANGLE/GL middle tier is deliberately not
    /// implemented: headless EGL creation is raw P/Invoke against libEGL.dll
    /// with real risk of getting an attribute list wrong, and D3D12 already
    /// covers the Windows target. Revisit only if a machine turns up where
    /// D3D12 device creation itself fails but GL would have worked.
    ///
    /// ADAPTER SELECTION IS EXPLICIT AND LOGGED, not incidental. Every
    /// enumerated adapter is listed at startup, and RenderSettings.
    /// GpuAdapterIndex chooses one (null = first non-software, the historical
    /// behaviour). This matters more than it looks: on a hybrid machine the
    /// first non-software adapter can be the integrated GPU or the discrete
    /// one depending on enumeration order, power settings and BIOS mode, so
    /// an unchanged build can execute the entire compositor on a different
    /// vendor's shader compiler from one boot to the next. A block-corruption
    /// bug was once chased for a long time before anyone asked which GPU was
    /// actually running the shader — the answer turned out to be the whole
    /// question. Making the choice explicit, and printing the roster, is why
    /// that is now a one-run experiment instead of an investigation.
    ///
    /// ENVIRONMENT FINGERPRINT: the adapter identity, its user-mode driver
    /// version, the actually-loaded SkiaSharp assembly and the OS build are
    /// logged once per GPU session. These are the things that can change
    /// underneath a build that has not changed, and having them in the log
    /// makes "nothing changed" verifiable instead of assumed.
    ///
    /// ADAPTER LIFETIME: SkiaSharp's D3D12 backend does not take its own
    /// reference on the raw native pointers handed to it via
    /// GRD3DBackendContext — it borrows them and keeps using the adapter for
    /// the GRContext's entire life. So the adapter backing a live GRContext
    /// is owned by this class and disposed only in Dispose(); every other
    /// enumerated adapter is released immediately. (An earlier version
    /// disposed all of them, including the live one, which is a
    /// use-after-free; and the version before that disposed none of them,
    /// which is a leak. Both are fixed here.)
    ///
    /// DISPOSE WAITS FOR THE GPU TO IDLE BEFORE TEARING DOWN THE DEVICE:
    /// added while chasing a real scrub-lockup investigation (a candidate
    /// theory at the time — see git/project history and Playback's own
    /// remarks — was that recreating a D3D12 device shortly after disposing
    /// the previous one could race that previous device's own driver-side
    /// teardown). Real-hardware testing after this fix showed the reported
    /// freeze was UNCHANGED — so this specific race is NOT confirmed as (or
    /// ruled out as) a real contributor to that bug; the actual cause turned
    /// out to be something else entirely (cross-thread GRContext usage — see
    /// Playback's class remarks, GPU WORK MUST STAY ON ONE THREAD, and
    /// GpuThreadDispatcher). Kept anyway on its own independent merits: it
    /// is real, cheap (a one-time cost at session teardown, not a hot path),
    /// and closes a genuine gap — the previous Dispose() gave no guarantee
    /// the GPU had actually finished outstanding work before the device/
    /// queue/adapter/factory backing it were destroyed. Deliberately NOT the
    /// same mistake as the per-Return sync SkSurfacePool's own remarks warn
    /// against reintroducing (that was a hot per-frame path that serialized
    /// CPU against GPU on every single surface return and bought nothing;
    /// this runs once per GpuContext teardown).
    ///
    /// UNVERIFIED SURFACE, flagged honestly: GRD3DBackendContext's shape and
    /// the Vortice call signatures below are written from familiarity with
    /// those libraries, not compile-tested here. Failures fall through the
    /// try/catch to software with a logged reason rather than producing a
    /// broken context. The same honesty flag applies to GRContext.Submit's
    /// exact signature/behavior in the currently-referenced SkiaSharp
    /// version — wrapped in its own try/catch below so a signature mismatch
    /// or an unexpected throw there degrades to a logged warning rather than
    /// blocking teardown outright.
    /// </summary>
    internal sealed class GpuContext : IDisposable
    {
        public GRContext? GRContext { get; }

        private readonly IDXGIFactory4? _factory;
        private readonly IDXGIAdapter1? _adapter;
        private readonly ID3D12Device? _device;
        private readonly ID3D12CommandQueue? _queue;

        private GpuContext(
            GRContext? grContext, IDXGIFactory4? factory, IDXGIAdapter1? adapter,
            ID3D12Device? device, ID3D12CommandQueue? queue)
        {
            GRContext = grContext;
            _factory = factory;
            _adapter = adapter;
            _device = device;
            _queue = queue;
        }

        /// <summary>
        /// Creates the compositor's GPU context. `adapterIndex` is
        /// RenderSettings.GpuAdapterIndex: null selects the first
        /// non-software adapter, an explicit index selects that adapter.
        /// </summary>
        public static GpuContext Create(HardwareAccelerator hwAccel, int? adapterIndex = null)
        {
            if (hwAccel != HardwareAccelerator.GPU)
                return new GpuContext(null, null, null, null, null);

            GpuContext? d3d12 = TryCreateD3D12(adapterIndex);
            if (d3d12 != null)
            {
                EditSharpConfig.Logger.LogVerbose("Composite: using D3D12 GRContext.");
                return d3d12;
            }

            EditSharpConfig.Logger.LogWarning(
                "HardwareAccelerator.GPU requested, but no usable D3D12 adapter was found" +
                (adapterIndex.HasValue
                    ? $" for RenderSettings.GpuAdapterIndex={adapterIndex.Value}"
                    : "") +
                " — the composite stage is running software raster. Decode and encode " +
                "hardware acceleration are unaffected by this.");

            return new GpuContext(null, null, null, null, null);
        }

        private static GpuContext? TryCreateD3D12(int? adapterIndex)
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

                    bool keepAlive = false;
                    try
                    {
                        AdapterDescription1 desc = adapter.Description1;
                        bool software = (desc.Flags & AdapterFlags.Software) != 0;

                        // Roster line for every adapter, so a valid
                        // GpuAdapterIndex can be read off the log rather
                        // than guessed at.
                        EditSharpConfig.Logger.Log(
                            $"Composite: adapter[{i}] '{desc.Description}' " +
                            $"vendor=0x{desc.VendorId:X4} device=0x{desc.DeviceId:X4} " +
                            $"vram={desc.DedicatedVideoMemory / (1024 * 1024)}MB" +
                            (software ? " [software]" : ""));

                        if (software) continue;

                        if (adapterIndex.HasValue && i != (uint)adapterIndex.Value)
                            continue;

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
                            {
                                LogEnvironmentFingerprint(adapter, i);
                                keepAlive = true; // see ADAPTER LIFETIME in the class remarks
                                return new GpuContext(grContext, factory, adapter, device, queue);
                            }

                            queue.Dispose();
                            device.Dispose();
                            queue = null;
                            device = null;
                        }
                    }
                    finally
                    {
                        if (!keepAlive) adapter.Dispose();
                    }
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

        /// <summary>
        /// Records everything about the runtime graphics environment that
        /// can change underneath an unchanged build — see the class remarks.
        /// </summary>
        private static void LogEnvironmentFingerprint(IDXGIAdapter1 adapter, uint index)
        {
            try
            {
                AdapterDescription1 desc = adapter.Description1;

                string driver = "unavailable";
                try
                {
                    // User-mode driver version. NVIDIA's user-facing number
                    // (e.g. 576.90) lives in the low half, so both the raw
                    // and decoded forms are recorded.
                    if (adapter.CheckInterfaceSupport(typeof(IDXGIDevice).GUID, out long umd).Success)
                    {
                        long sub = (umd >> 16) & 0xFFFF;
                        long build = umd & 0xFFFF;
                        driver =
                            $"raw=0x{umd:X16} " +
                            $"({(umd >> 48) & 0xFFFF}.{(umd >> 32) & 0xFFFF}.{sub}.{build}) " +
                            $"nvidia-style~{(((sub % 10) * 10000) + build) / 100.0:F2}";
                    }
                }
                catch (Exception ex)
                {
                    driver = $"query failed: {ex.Message}";
                }

                var skia = typeof(SKSurface).Assembly.GetName();
                string skiaInfo = skia.Version?.ToString() ?? "unknown";
                try
                {
                    string loc = typeof(SKSurface).Assembly.Location;
                    if (!string.IsNullOrEmpty(loc)) skiaInfo += $" @ {loc}";
                }
                catch { /* single-file publish exposes no Location */ }

                EditSharpConfig.Logger.Log(
                    "=== EditSharp GPU environment ===" + Environment.NewLine +
                    $"  Adapter[{index}]  : {desc.Description}" + Environment.NewLine +
                    $"  Vendor/Device : 0x{desc.VendorId:X4} / 0x{desc.DeviceId:X4}" + Environment.NewLine +
                    $"  Dedicated VRAM: {desc.DedicatedVideoMemory / (1024 * 1024)} MB" + Environment.NewLine +
                    $"  UMD driver    : {driver}" + Environment.NewLine +
                    $"  SkiaSharp     : {skiaInfo}" + Environment.NewLine +
                    $"  OS            : {RuntimeInformation.OSDescription}" + Environment.NewLine +
                    $"  Process arch  : {RuntimeInformation.ProcessArchitecture}" + Environment.NewLine +
                    "=================================");
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogVerbose($"GpuContext: environment fingerprint failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            // See class remarks, DISPOSE WAITS FOR THE GPU TO IDLE BEFORE
            // TEARING DOWN THE DEVICE. Force everything already submitted to
            // actually finish on the GPU before the device/queue/adapter/
            // factory it ran on are destroyed.
            if (GRContext != null)
            {
                try
                {
                    GRContext.Flush();
                    GRContext.Submit(true);
                }
                catch (Exception ex)
                {
                    // Never let a flush/submit failure block teardown
                    // outright — worst case here is falling back to the old
                    // (unsynchronized) behavior for this one Dispose, not a
                    // new hang.
                    EditSharpConfig.Logger.LogVerbose(
                        $"GpuContext.Dispose: GPU flush/submit before teardown threw: {ex.Message}");
                }
            }

            GRContext?.Dispose();
            _queue?.Dispose();
            _device?.Dispose();
            _adapter?.Dispose();
            _factory?.Dispose();
        }
    }
}