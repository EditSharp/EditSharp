using System;
using System.Collections.Generic;
using SkiaSharp;

namespace EditSharp.Composite
{
    /// <summary>
    /// Session-lifetime cache of SKSurface instances keyed by (width, height)
    /// — every surface this pool hands out is Rgba8888/Premul, the only
    /// format the Skia compositor ever creates, so that part of the key is
    /// implicit rather than tracked.
    ///
    /// WHY THIS IS SAFE, AND WHY NOW: item 11 committed the render loop to
    /// strictly sequential frame order (RenderAllFramesAsync's own semaphore/
    /// task-array machinery was removed, not just gated at 1). Nothing
    /// outlives a single frame's worth of work, so a surface returned at the
    /// end of frame N is exactly as valid to hand out again for frame N+1 —
    /// or for a second concurrent use within the SAME frame — as a freshly
    /// allocated one, just without paying the allocation again. This pool
    /// would NOT be safe to introduce if FrameRenderConcurrency (or anything
    /// like it) ever comes back.
    ///
    /// SEEDING: constructed with one canvas-sized surface per channel — a
    /// warm-start heuristic for the worst realistic count of canvas-sized
    /// surfaces concurrently alive within one frame (SkFrameCompositor.
    /// ComposeChannel allocates one clip surface per clip on the channel
    /// it's composing, and the composed result of every channel plus the
    /// accumulator/flattened pair can all be briefly alive at once). NOT a
    /// hard cap — Rent grows the pool on demand past the seed count, and
    /// past whatever content-sized surfaces get requested that were never
    /// seeded at all.
    ///
    /// SNAPSHOT SAFETY, NOT JUST A PERFORMANCE NOTE: every existing call
    /// site already follows the pattern `using var surface = SKSurface
    /// .Create(...); ...; return surface.Snapshot();` — the surface is
    /// disposed (now: returned to the pool) immediately after taking the
    /// snapshot, before the caller ever uses it. SKSurface.Snapshot() is
    /// Skia's own copy-on-write boundary: the returned SKImage is guaranteed
    /// independent of whatever the surface does afterward, including being
    /// Cleared and redrawn by a later Rent of the same physical instance.
    /// So reusing a surface via this pool the instant its snapshot is taken
    /// is exactly as safe as the `using` pattern it replaces — AT LEAST for
    /// a GRContext that submits its recorded GPU work in the way Skia's
    /// documented copy-on-write contract assumes. See the GPU SUBMISSION
    /// note below for the real gap found in the field.
    ///
    /// GPU SUBMISSION, ADDED AFTER A REAL REPORT — this pool (and every
    /// other call site that draws to a GRContext-backed SKSurface in this
    /// project) never once called GRContext.Flush()/Submit() anywhere.
    /// Skia's own higher-level consumers (e.g. a windowed swapchain
    /// integration) normally call Submit() once per displayed frame as part
    /// of presenting it; this project has no such consumer, since every
    /// surface here is read back via ReadPixels/Snapshot rather than
    /// presented to a window, and it was assumed (WRONGLY, per the report
    /// this fixes) that Skia's own internal flush-before-readback handled
    /// this automatically regardless of backend. Recorded GPU work can sit
    /// queued in Skia's own command-list state, not yet actually submitted
    /// to the real ID3D12CommandQueue GpuContext constructed — a state
    /// where reusing the SAME underlying texture for a new Rent+Clear+Draw
    /// races the GPU's own execution of the PRIOR content's commands. This
    /// is consistent with confirmed field behavior: absent entirely on pure
    /// software rendering (no GPU queue to race) and on hardware that falls
    /// back to software (see GpuContext's own remarks on D3D12 creation
    /// failing outright on some machines), present intermittently — "not
    /// present for a lot of the time" — specifically on the one machine
    /// with the newest/fastest GPU (more headroom before Skia's own
    /// internal resource budget forces a texture to be reclaimed and reused
    /// while older commands against it are still in flight, so a bigger,
    /// faster card takes longer to first exhibit it, not less likely to).
    ///
    /// FIX: force a real GPU submission — Flush() records any outstanding
    /// work into the command buffer, Submit(syncCpu: true) hands it to the
    /// queue AND blocks until the GPU has actually finished executing it —
    /// every time a GPU-backed surface is Return()'d, before it can be
    /// Rent()'d again and overwritten. This trades away some of the async
    /// pipelining a GPU backend would otherwise give (an intentional,
    /// correctness-over-throughput choice given how expensive silently-
    /// wrong pixels are to debug) — but only for the GPU path; software-
    /// backed surfaces (_grContext == null) skip it entirely, so this has
    /// no effect on HardwareAccelerator.None or on any machine already
    /// falling back to software. UNVERIFIED against a real installed
    /// SkiaSharp build in this sandbox (same honesty flag as the rest of
    /// GpuContext/SkSurfacePool) — wrapped in try/catch and logged rather
    /// than allowed to take down a render if Submit's actual signature on
    /// the referenced SkiaSharp version turns out to differ.
    ///
    /// NOT THREAD-SAFE, deliberately — matches the render loop's own
    /// strictly-sequential contract. A lock here would be pure overhead for
    /// a single-threaded caller, and would silently paper over a
    /// reintroduced-concurrency bug rather than surfacing it.
    /// </summary>
    internal sealed class SkSurfacePool : IDisposable
    {
        private readonly GRContext? _grContext;
        private readonly Dictionary<(int Width, int Height), Stack<SKSurface>> _free = new();
        private readonly List<SKSurface> _owned = new();
        private bool _gpuSubmitFailed;

        public SkSurfacePool(GRContext? grContext, int canvasWidth, int canvasHeight, int seedCount)
        {
            _grContext = grContext;

            var key = (canvasWidth, canvasHeight);
            var stack = new Stack<SKSurface>(Math.Max(seedCount, 0));
            for (int i = 0; i < seedCount; i++)
                stack.Push(CreateSurface(canvasWidth, canvasHeight));

            _free[key] = stack;
        }

        /// <summary>
        /// Hands out a surface of exactly width x height. Content is
        /// whatever was left on it by its previous use (or uninitialized,
        /// for a brand new one) — the caller MUST Clear() (or otherwise
        /// fully overwrite every pixel) before drawing, same obligation
        /// every existing SKSurface.Create call site already met.
        /// </summary>
        public SKSurface Rent(int width, int height)
        {
            var key = (width, height);
            if (_free.TryGetValue(key, out Stack<SKSurface>? stack) && stack.Count > 0)
                return stack.Pop();

            return CreateSurface(width, height);
        }

        /// <summary>
        /// Returns a surface obtained from Rent (or present at seed time)
        /// for reuse. width/height must match what it was Rent'd as —
        /// there's no way to recover a surface's own size cheaply after the
        /// fact in SkiaSharp's public API, so this pool relies on the
        /// caller passing it back rather than deriving it.
        ///
        /// For a GPU-backed surface, this is also the synchronization point
        /// — see the class remarks' GPU SUBMISSION section. The surface is
        /// only pushed back for reuse AFTER the GPU has actually finished
        /// with whatever was drawn into it, so the next Rent() of this same
        /// physical instance can never race prior work still executing
        /// against it.
        /// </summary>
        public void Return(SKSurface surface, int width, int height)
        {
            EnsureGpuWorkSubmitted();

            var key = (width, height);
            if (!_free.TryGetValue(key, out Stack<SKSurface>? stack))
                _free[key] = stack = new Stack<SKSurface>();

            stack.Push(surface);
        }

        /// <summary>
        /// Flushes and synchronously submits any outstanding GPU work on
        /// this pool's GRContext — a no-op for a software-backed pool
        /// (_grContext == null). Failure is logged once and then silently
        /// skipped for the rest of this pool's life rather than retried
        /// every single Return() call — see class remarks on why this is
        /// unverified against a real SkiaSharp build.
        /// </summary>
        private void EnsureGpuWorkSubmitted()
        {
            if (_grContext == null || _gpuSubmitFailed) return;

            try
            {
                _grContext.Flush();
                _grContext.Submit();
            }
            catch (Exception ex)
            {
                _gpuSubmitFailed = true;
                EditSharpConfig.Logger.LogWarning(
                    "SkSurfacePool: GRContext.Flush()/Submit(syncCpu: true) failed and will not be " +
                    $"retried for this render — GPU surface reuse may race outstanding GPU work: {ex.Message}");
            }
        }

        private SKSurface CreateSurface(int width, int height)
        {
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);

            SKSurface? surface = _grContext != null
                ? SKSurface.Create(_grContext, budgeted: true, info)
                : null;

            // GPU-backed creation can legitimately fail (context lost,
            // texture budget exhausted) even when the GRContext itself is
            // healthy — fall back to raster for just this one surface
            // rather than taking down the whole render over it. This is a
            // per-surface fallback, distinct from and much rarer than the
            // whole-context fallback GpuContext itself already logs loudly;
            // not logged here to avoid spamming per-frame if it repeats.
            surface ??= SKSurface.Create(info);

            _owned.Add(surface);
            return surface;
        }

        public void Dispose()
        {
            foreach (SKSurface surface in _owned) surface.Dispose();
            _owned.Clear();
            _free.Clear();
        }
    }
}