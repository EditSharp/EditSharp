using System;
using System.IO;
using SkiaSharp;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
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
    ///
    /// ADAPTER LIFETIME — a real bug found and fixed here, but NOT the
    /// cause of the block-corruption bug. It was believed to be the cause
    /// when this was written; that was wrong, and the claim is corrected
    /// rather than deleted so the reasoning trail stays honest. The
    /// corruption survived this fix, and was later shown to appear on
    /// UNCHANGED code that had previously rendered correctly (reverting to
    /// the first GPU commit did not help, with no SkiaSharp/ffmpeg version
    /// change), which points outside the codebase entirely — see the
    /// ENVIRONMENT FINGERPRINT section below. The lifetime fix stays
    /// because it is independently correct: a GRContext must not outlive
    /// the adapter it borrows. An earlier pass at a genuine COM
    /// leak (the adapter enumeration loop never called adapter.Dispose() at
    /// all) fixed the leak by disposing every enumerated IDXGIAdapter1
    /// unconditionally in a `finally`, including the one adapter actually
    /// used to build the successful device/queue/GRContext. That looked
    /// correct — GRContext.CreateDirect3D(backendContext) had already
    /// returned by that point — but SkiaSharp's D3D12 backend does NOT take
    /// its own reference on the raw native pointers handed to it via
    /// GRD3DBackendContext; it borrows them, and keeps calling back into the
    /// adapter for the GRContext's entire life (IDXGIAdapter3::
    /// QueryVideoMemoryInfo, used for GPU memory-budget tracking/eviction
    /// decisions, most notably whenever a pooled surface gets reused under
    /// memory pressure). Disposing our only reference to that adapter the
    /// instant CreateDirect3D() returned released the underlying COM object
    /// out from under a GRContext that was still going to dereference it —
    /// a real use-after-free, not merely a leak.
    ///
    /// (The reasoning that made this look like the culprit — GPU-path-only,
    /// reproducing on a second factory-fresh card, most visible against
    /// smooth noise — turned out to fit the actual environmental cause just
    /// as well, which is why it was convincing and still wrong.)
    ///
    /// FIX: GpuContext now holds onto (and owns) the successful adapter for
    /// its own entire lifetime, disposing it in Dispose() alongside
    /// _device/_queue/_factory — exactly like every other COM object this
    /// class already keeps alive on purpose. Every OTHER enumerated adapter
    /// (skipped WARP adapters, ones device creation failed on, ones
    /// GRContext creation itself failed on) is still disposed immediately,
    /// since none of those are ever touched again — only the one actually
    /// backing a live GRContext needs to outlive this method.
    /// </summary>
    internal sealed class GpuContext : IDisposable
    {
        public GRContext? GRContext { get; }

        private readonly IDXGIFactory4? _factory;
        private readonly IDXGIAdapter1? _adapter;
        private readonly ID3D12Device? _device;
        private readonly ID3D12CommandQueue? _queue;

        // ---------------------------------------------------------------
        // TEMPORARY DIAGNOSTIC — active investigation of the block-
        // corruption bug (confirmed deterministic: present on the very
        // first frame, every time, ruling out every timing/reuse theory
        // tried so far). Opt-in via EDITSHARP_D3D12_DEBUG=1. Enables the
        // real D3D12 validation layer and surfaces whatever it reports —
        // actual GPU-driver-verified API misuse (wrong resource states,
        // bad descriptor/root-signature binding, missing barriers) rather
        // than another guess from this side. UNVERIFIED against a real
        // installed Vortice.Direct3D12 build, same honesty flag as the
        // rest of this class — wrapped so a signature mismatch just
        // disables itself with a logged reason instead of crashing the
        // render. Remove once root-caused.
        // ---------------------------------------------------------------

        // ---------------------------------------------------------------
        // ENVIRONMENT FINGERPRINT + ADAPTER OVERRIDE.
        //
        // Added after the decisive observation that UNCHANGED code which
        // previously rendered correctly began producing block corruption —
        // reverting all the way to the first GPU commit did not help, and
        // no SkiaSharp/ffmpeg version changed. A bug that appears without a
        // code change is environmental, so the environment has to be
        // recorded as precisely as the code is.
        //
        // EDITSHARP_D3D12_ADAPTER=<index> forces a specific DXGI adapter
        // instead of "first non-software". On a hybrid machine that is the
        // only way to A/B the discrete and integrated GPU with the same
        // build, same blueprint, same moment — the cleanest possible test
        // of whether this is vendor/driver-specific.
        // ---------------------------------------------------------------
        private static readonly string? AdapterOverrideRaw =
            Environment.GetEnvironmentVariable("EDITSHARP_D3D12_ADAPTER");

        private static int AdapterOverrideIndex =>
            int.TryParse(AdapterOverrideRaw, out int idx) ? idx : -1;

        /// <summary>
        /// Logs everything about the runtime graphics environment that
        /// could plausibly change underneath an unchanged build: the
        /// adapter identity, the user-mode driver version (the prime
        /// suspect), the actual loaded SkiaSharp assembly, and the OS
        /// build. Logged unconditionally — this is cheap, happens once per
        /// render session, and is exactly the information that was missing
        /// when the corruption first appeared.
        /// </summary>
        private static void LogEnvironmentFingerprint(IDXGIAdapter1 adapter, uint index)
        {
            try
            {
                AdapterDescription1 desc = adapter.Description1;

                string driver = "unavailable";
                try
                {
                    // The user-mode driver version. For NVIDIA the
                    // user-facing number (e.g. 576.90) is encoded in the
                    // low half, so both raw and decoded forms are logged.
                    if (adapter.CheckInterfaceSupport(typeof(IDXGIDevice).GUID, out long umd).Success)
                    {
                        long sub = (umd >> 16) & 0xFFFF;
                        long build = umd & 0xFFFF;
                        long nvidiaStyle = ((sub % 10) * 10000) + build;
                        driver =
                            $"raw=0x{umd:X16} " +
                            $"({(umd >> 48) & 0xFFFF}.{(umd >> 32) & 0xFFFF}.{sub}.{build}) " +
                            $"nvidia-style~{nvidiaStyle / 100.0:F2}";
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
                    if (!string.IsNullOrEmpty(loc))
                        skiaInfo += $" @ {loc}";
                }
                catch { /* single-file publish has no Location */ }

                EditSharpConfig.Logger.LogWarning(
                    "=== EditSharp GPU environment fingerprint ===\n" +
                    $"  Adapter[{index}]  : {desc.Description}\n" +
                    $"  Vendor/Device : 0x{desc.VendorId:X4} / 0x{desc.DeviceId:X4} " +
                    $"(rev 0x{desc.Revision:X}, subsys 0x{desc.SubsystemId:X})\n" +
                    $"  Dedicated VRAM: {desc.DedicatedVideoMemory / (1024 * 1024)} MB\n" +
                    $"  UMD driver    : {driver}\n" +
                    $"  SkiaSharp     : {skiaInfo}\n" +
                    $"  OS            : {Environment.OSVersion} / {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n" +
                    $"  Process arch  : {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\n" +
                    "============================================");
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogWarning($"GpuContext: environment fingerprint failed: {ex.Message}");
            }
        }

        private static readonly bool DebugLayerRequested =
            Environment.GetEnvironmentVariable("EDITSHARP_D3D12_DEBUG") == "1";

        // Where drained messages get written — defaults next to wherever
        // the process runs from if EDITSHARP_D3D12_DEBUG_LOG isn't also
        // set, so this works with zero extra configuration beyond the one
        // flag above. A real file (not the app's own log stream) so it's
        // trivial to open/attach/paste back whole, independent of whatever
        // else EditSharpConfig.Logger is wired to.
        private static readonly string DebugLogPath =
            Environment.GetEnvironmentVariable("EDITSHARP_D3D12_DEBUG_LOG") ?? "editsharp_d3d12_debug.log";

        private ID3D12InfoQueue? _infoQueue;
        private StreamWriter? _debugLogWriter;

        private GpuContext(
            GRContext? grContext, IDXGIFactory4? factory, IDXGIAdapter1? adapter,
            ID3D12Device? device, ID3D12CommandQueue? queue, ID3D12InfoQueue? infoQueue = null)
        {
            GRContext = grContext;
            _factory = factory;
            _adapter = adapter;
            _device = device;
            _queue = queue;
            _infoQueue = infoQueue;

            if (_infoQueue != null)
            {
                try
                {
                    // One fresh file per render session (overwrite, not
                    // append) — no stale messages from a previous run to
                    // sift through. AutoFlush so a message is on disk the
                    // instant it's written, even if the process is later
                    // killed rather than exited cleanly.
                    _debugLogWriter = new StreamWriter(DebugLogPath, append: false) { AutoFlush = true };
                    _debugLogWriter.WriteLine($"# EditSharp D3D12 debug-layer log — session started {DateTime.Now:O}");
                    EditSharpConfig.Logger.LogWarning(
                        $"Composite: D3D12 debug-layer messages will be written to '{Path.GetFullPath(DebugLogPath)}'.");
                }
                catch (Exception ex)
                {
                    EditSharpConfig.Logger.LogWarning(
                        $"Composite: could not open '{DebugLogPath}' for D3D12 debug-layer output " +
                        $"({ex.Message}) — falling back to the regular log instead.");
                    _debugLogWriter = null;
                }
            }
        }

        /// <summary>
        /// TEMPORARY DIAGNOSTIC — see the class remarks above _infoQueue.
        /// Drains every D3D12 validation-layer message queued since the
        /// last drain into the debug log file (or, if that file couldn't
        /// be opened, the regular logger as a fallback), tagged with
        /// `context` (e.g. a frame index) so messages can be lined up
        /// against exactly which draw produced them. No-op if the debug
        /// layer wasn't requested or couldn't be wired up. Deliberately NOT
        /// rate-limited — which frames/contexts messages appear on is
        /// itself part of the evidence right now.
        /// </summary>
        public void DrainAndLogD3D12DebugMessages(string context)
        {
            if (_infoQueue == null) return;

            try
            {
                ulong count = _infoQueue.NumStoredMessages;
                for (ulong i = 0; i < count; i++)
                {
                    var message = _infoQueue.GetMessage(i);
                    string line =
                        $"[{context}] {message.Severity}/{message.Category} (#{message.Id}): {message.Description}";

                    if (_debugLogWriter != null) _debugLogWriter.WriteLine(line);
                    else EditSharpConfig.Logger.LogWarning($"[D3D12 DEBUG LAYER] {line}");
                }
                _infoQueue.ClearStoredMessages();
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogWarning(
                    $"GpuContext: draining D3D12 debug messages failed — disabling for the rest of " +
                    $"this render (likely an API-shape mismatch against the real Vortice/D3D12 build, " +
                    $"see this class's own honesty flag): {ex.Message}");
                _infoQueue = null;
            }
        }

        public static GpuContext Create(HardwareAccelerator hwAccel)
        {
            if (hwAccel != HardwareAccelerator.GPU)
                return new GpuContext(null, null, null, null, null);

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

            return new GpuContext(null, null, null, null, null);
        }

        private static GpuContext? TryCreateD3D12()
        {
            IDXGIFactory4? factory = null;
            ID3D12Device? device = null;
            ID3D12CommandQueue? queue = null;

            try
            {
                // TEMPORARY DIAGNOSTIC — see the class remarks near
                // _infoQueue. Must happen BEFORE device creation: the debug
                // layer only instruments devices created after it's been
                // enabled.
                if (DebugLayerRequested)
                {
                    try
                    {
                        if (D3D12.D3D12GetDebugInterface<ID3D12Debug>(out ID3D12Debug? debugInterface).Success
                            && debugInterface != null)
                        {
                            debugInterface.EnableDebugLayer();
                            debugInterface.Dispose();
                            EditSharpConfig.Logger.LogWarning(
                                "Composite: D3D12 debug/validation layer enabled (EDITSHARP_D3D12_DEBUG=1).");
                        }
                        else
                        {
                            EditSharpConfig.Logger.LogWarning(
                                "Composite: EDITSHARP_D3D12_DEBUG=1 set, but D3D12GetDebugInterface failed — " +
                                "is the Windows 'Graphics Tools' optional feature installed? Continuing " +
                                "without the validation layer.");
                        }
                    }
                    catch (Exception ex)
                    {
                        EditSharpConfig.Logger.LogWarning(
                            $"Composite: failed to enable the D3D12 debug layer, continuing without it: {ex.Message}");
                    }
                }

                factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();

                for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success; i++)
                {
                    if (adapter == null) continue;

                    // Full adapter listing, so EDITSHARP_D3D12_ADAPTER can be
                    // aimed at a specific GPU without guessing. On a hybrid
                    // machine the FIRST non-software adapter is frequently the
                    // INTEGRATED GPU — which means this selection logic can
                    // silently decide which vendor's shader compiler runs the
                    // whole compositor, and a machine reconfigured from hybrid
                    // to dGPU-only starts executing the same unchanged code on
                    // a completely different GPU. That is not a hypothetical:
                    // it is the leading explanation for a block-corruption bug
                    // that appeared with no code change and survived reverting
                    // to the first GPU commit.
                    try
                    {
                        AdapterDescription1 d = adapter.Description1;
                        bool software = (d.Flags & AdapterFlags.Software) != 0;
                        EditSharpConfig.Logger.LogWarning(
                            $"Composite: adapter[{i}] '{d.Description}' " +
                            $"vendor=0x{d.VendorId:X4} device=0x{d.DeviceId:X4} " +
                            $"vram={d.DedicatedVideoMemory / (1024 * 1024)}MB" +
                            (software ? " [SOFTWARE — skipped]" : ""));
                    }
                    catch { /* listing is diagnostic only, never fatal */ }

                    // `keepAlive` tracks whether THIS adapter is the one
                    // that ends up backing a successful GRContext — if so,
                    // GpuContext takes ownership of it for its own lifetime
                    // (see class remarks on why: the D3D12 GRContext keeps
                    // dereferencing it long after this method returns).
                    // Every other adapter reaching the `finally` below
                    // (WARP-skipped, failed device creation, failed
                    // GRContext creation) is disposed immediately — nothing
                    // else ever touches it again.
                    bool keepAlive = false;
                    try
                    {
                        if ((adapter.Description1.Flags & AdapterFlags.Software) != 0)
                            continue;

                        // Adapter override: skip everything that isn't the
                        // requested index. See this class's ENVIRONMENT
                        // FINGERPRINT remarks.
                        int overrideIndex = AdapterOverrideIndex;
                        if (overrideIndex >= 0 && i != (uint)overrideIndex)
                        {
                            EditSharpConfig.Logger.LogVerbose(
                                $"Composite: skipping adapter[{i}] '{adapter.Description1.Description}' " +
                                $"(EDITSHARP_D3D12_ADAPTER={overrideIndex}).");
                            continue;
                        }

                        if (D3D12.D3D12CreateDevice(adapter, Vortice.Direct3D.FeatureLevel.Level_11_0, out device).Success
                            && device != null)
                        {
                            var queueDesc = new CommandQueueDescription(CommandListType.Direct);
                            queue = device.CreateCommandQueue(queueDesc);

                            // TEMPORARY DIAGNOSTIC — see remarks near
                            // _infoQueue. Best-effort: absence or failure
                            // here just means DrainAndLogD3D12DebugMessages
                            // stays a no-op, never a hard failure.
                            ID3D12InfoQueue? infoQueue = null;
                            if (DebugLayerRequested)
                            {
                                try { infoQueue = device.QueryInterfaceOrNull<ID3D12InfoQueue>(); }
                                catch (Exception ex)
                                {
                                    EditSharpConfig.Logger.LogWarning(
                                        $"Composite: could not query ID3D12InfoQueue from the device: {ex.Message}");
                                }
                            }

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
                                keepAlive = true;
                                return new GpuContext(grContext, factory, adapter, device, queue, infoQueue);
                            }

                            infoQueue?.Dispose();

                            // Context creation itself failed despite a working
                            // device/queue — clean up this attempt and report
                            // failure to the caller, which logs and falls back.
                            // `adapter` falls through to the finally below and
                            // is disposed normally (keepAlive is still false).
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

        public void Dispose()
        {
            GRContext?.Dispose();
            _infoQueue?.Dispose();
            _debugLogWriter?.Dispose();
            _queue?.Dispose();
            _device?.Dispose();
            _adapter?.Dispose();
            _factory?.Dispose();
        }
    }
}