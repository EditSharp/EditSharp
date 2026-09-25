using System;
using System.Collections.Generic;
using SkiaSharp;

namespace EditSharp.Compositing.Gpu
{
    /// <summary>Reuses RGBA8888 premultiplied surfaces by size for a session, instead of allocating new ones every frame.</summary>
    /// <remarks>
    /// It starts with one frame-sized surface per channel and grows on demand.
    /// Callers snapshot a surface and return it straight away; a snapshot is
    /// independent of the surface, so reuse is as safe as a new surface. A
    /// forced GPU sync on every return was once added here while chasing
    /// corruption (the cause was elsewhere) and removed: it stalls the CPU on
    /// the GPU every time. Not thread-safe: use it from the thread that owns its
    /// GRContext.
    /// </remarks>
    internal sealed class SurfacePool : IDisposable
    {
        private readonly GRContext? _grContext;
        private readonly Dictionary<(int Width, int Height), Stack<SKSurface>> _free = new();
        private readonly List<SKSurface> _owned = new();

        public SurfacePool(GRContext? grContext, int canvasWidth, int canvasHeight, int seedCount)
        {
            _grContext = grContext;

            var key = (canvasWidth, canvasHeight);
            var stack = new Stack<SKSurface>(Math.Max(seedCount, 0));
            for (int i = 0; i < seedCount; i++)
                stack.Push(CreateSurface(canvasWidth, canvasHeight));

            _free[key] = stack;
        }

        //a surface of exactly width x height holding whatever was drawn last; clear it before drawing
        public SKSurface Rent(int width, int height)
        {
            var key = (width, height);
            if (_free.TryGetValue(key, out Stack<SKSurface>? stack) && stack.Count > 0)
                return stack.Pop();

            return CreateSurface(width, height);
        }

        //takes back a rented surface; the size must be what it was rented at, since SkiaSharp can't report it cheaply
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

            //creating a GPU surface can fail on a healthy context (texture budget used up): fall back to raster for
            //this one surface, unlogged so a repeat doesn't flood the log
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