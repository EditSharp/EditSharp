using System;
using System.Runtime.InteropServices;
using SkiaSharp;
using EditSharp.Video;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace EditSharp.Compositing.Gpu
{
    /// <summary>The GRContext the compositor draws with: D3D12 when available, otherwise software.</summary>
    /// <remarks>
    /// Every adapter is logged at startup and <see cref="Rendering.RenderSettings.GpuAdapterIndex"/>
    /// picks one; on a hybrid machine the first hardware adapter can change between
    /// boots. The adapter's identity, driver version, SkiaSharp version and OS build
    /// are logged once per session, so a change under an unchanged build shows. The
    /// adapter in use is kept until Dispose, because Skia's D3D12 backend borrows it
    /// without a reference of its own; the others are released at once. Dispose waits
    /// for the GPU to finish submitted work before destroying the device. Any failure
    /// setting up D3D12 falls back to software with the reason logged.
    /// </remarks>
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

        //the compositor's GPU context; `adapterIndex` null picks the first hardware adapter
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
                "; the composite stage is running software raster. Decode and encode " +
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

                        //one line per adapter, so a valid GpuAdapterIndex can be read off the log
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

        //logs what can change underneath an unchanged build: adapter, driver, SkiaSharp, OS
        private static void LogEnvironmentFingerprint(IDXGIAdapter1 adapter, uint index)
        {
            try
            {
                AdapterDescription1 desc = adapter.Description1;

                string driver = "unavailable";
                try
                {
                    //the user-mode driver version; NVIDIA's familiar number (such as 576.90) is in the low half, so both forms are logged
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
            //let everything submitted finish before the device it runs on is destroyed
            if (GRContext != null)
            {
                try
                {
                    GRContext.Flush();
                    GRContext.Submit(true);
                }
                catch (Exception ex)
                {
                    //a failed flush mustn't block teardown
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