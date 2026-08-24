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
    /// WHY THIS IS SAFE: the render loop is strictly sequential (see
    /// Renderer.RenderAllFramesAsync). Nothing outlives a single frame's
    /// worth of work, so a surface returned at the end of frame N is exactly
    /// as valid to hand out again for frame N+1 — or for a second concurrent
    /// use within the SAME frame — as a freshly allocated one, just without
    /// paying the allocation again. This pool would NOT be safe if
    /// frame-level render concurrency were ever reintroduced.
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
    /// SNAPSHOT SAFETY, NOT JUST A PERFORMANCE NOTE: every call site follows
    /// the pattern `surface = pool.Rent(...); ...; snapshot = surface
    /// .Snapshot(); pool.Return(surface, ...)` — the surface goes back the
    /// moment its snapshot is taken, before the caller uses the snapshot.
    /// SKSurface.Snapshot() is Skia's own copy-on-write boundary: the
    /// returned SKImage is guaranteed independent of whatever the surface
    /// does afterward, including being Cleared and redrawn by a later Rent
    /// of the same physical instance. So pooled reuse is exactly as safe as
    /// the `using var surface = SKSurface.Create(...)` pattern it replaces.
    ///
    /// HISTORY, worth knowing before "fixing" this again: a forced
    /// GRContext.Flush() + Submit(syncCpu: true) on every Return, and a
    /// forced clear-and-flush at surface creation, were both added here
    /// while chasing a block-corruption bug. Neither was the cause. The
    /// Submit(syncCpu: true) in particular serialises the CPU against the
    /// GPU on every single surface return, which throws away most of the
    /// benefit of having a GPU backend at all, so both were removed once the
    /// real cause was found (a precision hazard in SkNoiseClip's shader —
    /// see that file). Skia flushes as needed for readback on its own; do
    /// not reintroduce a per-Return sync without evidence that it is
    /// actually required.
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
        /// Hands out a surface of exactly width x height. Content is whatever
        /// was left on it by its previous use (or uninitialized, for a brand
        /// new one) — the caller MUST Clear() (or otherwise fully overwrite
        /// every pixel) before drawing, the same obligation every
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
        /// for reuse. width/height must match what it was Rent'd as — there
        /// is no cheap way to recover a surface's own size after the fact in
        /// SkiaSharp's public API, so this pool relies on the caller passing
        /// it back rather than deriving it.
        /// </summary>
        public void Return(SKSurface surface, int width, int height)
        {
            var key = (width, height);
            if (!_free.TryGetValue(key, out Stack<SKSurface>? stack))
                _free[key] = stack = new Stack<SKSurface>();
 
            stack.Push(surface);
        }
 
        private SKSurface CreateSurface(int width, int height)
        {
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
 
            SKSurface? surface = _grContext != null
                ? SKSurface.Create(_grContext, budgeted: true, info)
                : null;
 
            // GPU-backed creation can legitimately fail (context lost,
            // texture budget exhausted) even when the GRContext itself is
            // healthy — fall back to raster for just this one surface rather
            // than taking down the whole render over it. This is a
            // per-surface fallback, distinct from and much rarer than the
            // whole-context fallback GpuContext already logs loudly; not
            // logged here to avoid spamming per-frame if it repeats.
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
 