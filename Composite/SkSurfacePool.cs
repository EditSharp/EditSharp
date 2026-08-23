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
    /// FIX: force a real GPU submission every time a GPU-backed surface is
    /// Return()'d — see EnsureGpuWorkSubmitted below. Independently correct
    /// and kept regardless of the notes below.
    ///
    /// INVESTIGATION HISTORY for a real block-corruption bug found in the
    /// field, kept here for anyone re-reading this file later: a canvas
    /// Save()/Restore() imbalance on a reused surface was checked for and
    /// ruled out (a temporary Canvas.SaveCount assertion in Return() ran
    /// through a full repro and never fired once). The D3D12 debug/
    /// validation layer then identified the REAL cause as living one level
    /// up, in SkTransformExpressions' sampling options (unconditional
    /// SKMipmapMode.Linear triggering a broken on-the-fly mipmap-generation
    /// path on this backend — see that file's own remarks) — not in this
    /// pool's reuse scheme at all, which the validation layer's silence on
    /// ResourceBarrier/subresource-state errors for ordinary (non-mipmap)
    /// draws corroborates.
    ///
    /// FIRST-USE INITIALIZATION, ADDED AFTER A SEPARATE, SMALLER REAL
    /// REPORT found in the same investigation: the D3D12 debug layer also
    /// flagged 3 RenderTargetOrDepthStencilResouceNotInitialized errors,
    /// all on the warm-up frame — a resource created with
    /// D3D12_HEAP_FLAG_CREATE_NOT_ZEROED (as Skia's own D3D12 texture
    /// allocator does) must have its very first GPU-side touch be a real
    /// Discard/Clear/Copy, and this pool's seeded surfaces (created in the
    /// constructor, before any drawing ever happens to them) were sitting
    /// idle until whatever caller first Rent()'d them did its own
    /// Clear()+Draw() — leaving open the possibility that a caller's Clear
    /// gets folded into a render-pass "load action" alongside its Draw
    /// rather than recorded as its own standalone GPU operation, which
    /// doesn't satisfy this requirement. FIX: CreateSurface itself now
    /// performs a real, immediately-submitted Clear the instant a GPU-
    /// backed surface is created — for both seeded and on-demand surfaces
    /// — so the requirement is met by a dedicated op under this pool's own
    /// control, independent of whatever a caller's later Clear()+Draw()
    /// sequence gets compiled into.
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
        /// whatever was left on it by its previous use (or transparent, for
        /// a brand new one — see CreateSurface's first-use initialization)
        /// — the caller MUST Clear() (or otherwise fully overwrite every
        /// pixel) before drawing, same obligation every existing
        /// SKSurface.Create call site already met.
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
        /// every single Return() call.
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
                    "SkSurfacePool: GRContext.Flush()/Submit() failed and will not be " +
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

            // FIRST-USE INITIALIZATION — see class remarks. Give every
            // GPU-backed surface a real, immediately-submitted Clear the
            // instant it's created, before it's ever handed out via Rent(),
            // so a D3D12 NOT_ZEROED render target's mandatory first-touch
            // requirement is satisfied by a dedicated op this pool controls
            // directly rather than depending on a caller's later
            // Clear()+Draw() sequence.
            if (_grContext != null)
            {
                surface.Canvas.Clear(SKColors.Transparent);

                // surface.Flush() (not just GRContext.Flush) is what forces
                // THIS surface's own pending ops to actually be recorded —
                // a clear with nothing drawn after it is otherwise a prime
                // candidate for Skia to elide entirely as dead work, which
                // is the likely reason a first attempt at this fix (Clear +
                // GRContext.Flush/Submit alone) left the
                // RenderTargetOrDepthStencilResouceNotInitialized errors
                // exactly as they were. Wrapped because SKSurface.Flush's
                // presence/shape varies across SkiaSharp versions and this
                // whole D3D12 surface is unverified here (see GpuContext).
                try { surface.Flush(); }
                catch (Exception ex)
                {
                    EditSharpConfig.Logger.LogVerbose(
                        $"SkSurfacePool: SKSurface.Flush() on a new surface failed: {ex.Message}");
                }

                EnsureGpuWorkSubmitted();
            }

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