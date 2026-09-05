using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace EditSharp.Composite
{
    /// <summary>
    /// Confines every unit of work passed to Run/RunAsync to ONE dedicated
    /// background thread, for as long as this dispatcher lives — nothing
    /// else.
    ///
    /// WHY THIS EXISTS — FOUND WHILE DIAGNOSING A REAL, REPRODUCIBLE SCRUB
    /// LOCKUP (see Playback's own class remarks, GPU WORK MUST STAY ON ONE
    /// THREAD, for the full story): two earlier fixes in the same
    /// investigation (adding ConfigureAwait(false) throughout the scrub
    /// async chain, and making GpuContext.Dispose() wait for the GPU to
    /// idle before tearing down the device) did NOT change the freeze at
    /// all — the actual EditSharp.Playback consumer app (a Godot C# game)
    /// calls Playback.ScrubToAsync fire-and-forget from a slider's
    /// ValueChanged signal, never blocking synchronously on it, which rules
    /// out the sync-over-async deadlock theory those two fixes were built
    /// on. Godot's C# integration installs NO SynchronizationContext, so
    /// `await` in this codebase was ALREADY resuming on arbitrary
    /// ThreadPool threads before either fix — meaning every GPU call this
    /// class's own GRContext/SKSurface/GpuContext objects ever made across
    /// an `await` boundary could already land on a DIFFERENT OS thread than
    /// the one before it, entirely independent of ConfigureAwait. Skia's
    /// GrDirectContext (GRContext in SkiaSharp) is not documented as safe
    /// for that usage pattern — sequential-but-cross-thread access to one
    /// GRContext, its SKSurfaces, and the D3D12 command queue backing it,
    /// with no per-thread pinning, is a real, plausible source of exactly
    /// the reported symptom: deterministic-looking (the same call shape
    /// tends to get scheduled onto threads the same way run to run), and
    /// more likely to actually manifest the more real GPU work a call
    /// submits (a long scrub jump needs a colder read plus, potentially, a
    /// different composite shape) — matching "short first scrub always
    /// survives, long first scrub always hangs, then anything is fine"
    /// precisely. FIX: every GPU-touching call this dispatcher is used for
    /// (GpuContext.Create, SkSurfacePool construction, SkFrameCompositor.
    /// RenderFrame, and GpuContext.Dispose()) is marshaled onto this single
    /// dedicated thread via Run/RunAsync, so the underlying GRContext is
    /// created, used, and destroyed by the exact same OS thread for its
    /// entire life — eliminating the hazard structurally instead of hoping
    /// the ThreadPool schedules continuations favorably.
    ///
    /// NOT a general-purpose thread pool — one dispatcher instance is one
    /// thread, work queued to it runs strictly IN ORDER (FIFO), and it is
    /// meant to be paired 1:1 with the GPU resource(s) it protects (see
    /// Playback's persistent `_scrubGpuThread`, created once per Playback
    /// instance and disposed only with it — see class remarks, GPU WORK
    /// MUST STAY ON ONE THREAD, for why the scrub GpuContext is no longer
    /// disposed/recreated per session either).
    ///
    /// UNVERIFIED ON REAL HARDWARE, flagged honestly like every other GPU-
    /// adjacent fix in this investigation: this is a structural fix for a
    /// well-reasoned hazard, not a confirmed root cause — report back after
    /// real-machine testing.
    /// </summary>
    internal sealed class GpuThreadDispatcher : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private volatile bool _disposed;

        public GpuThreadDispatcher(string threadName)
        {
            _thread = new Thread(RunLoop)
            {
                IsBackground = true,
                Name = threadName,
            };
            _thread.Start();
        }

        private void RunLoop()
        {
            // GetConsumingEnumerable blocks this thread (and only this
            // thread) until work arrives or CompleteAdding() is called by
            // Dispose() — every Action queued via Run/RunAsync executes
            // here, on this same thread, one at a time, in the order it was
            // queued.
            foreach (Action action in _queue.GetConsumingEnumerable())
            {
                action();
            }
        }

        /// <summary>Runs `action` on this dispatcher's dedicated thread and returns once it completes (or throws).</summary>
        public Task RunAsync(Action action)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!TryEnqueue(() =>
            {
                try { action(); tcs.SetResult(); }
                catch (Exception ex) { tcs.SetException(ex); }
            }))
            {
                tcs.SetException(new ObjectDisposedException(nameof(GpuThreadDispatcher)));
            }

            return tcs.Task;
        }

        /// <summary>Runs `func` on this dispatcher's dedicated thread and returns its result once it completes (or throws).</summary>
        public Task<T> RunAsync<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!TryEnqueue(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }))
            {
                tcs.SetException(new ObjectDisposedException(nameof(GpuThreadDispatcher)));
            }

            return tcs.Task;
        }

        private bool TryEnqueue(Action wrapped)
        {
            try
            {
                // BlockingCollection.Add throws InvalidOperationException
                // once CompleteAdding() has been called (Dispose() in
                // progress/finished) — treated as "dispatcher is gone,"
                // same as it being null, rather than crashing the caller.
                _queue.Add(wrapped);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// Stops accepting new work, waits for anything already queued to
        /// finish, then joins the dedicated thread. Any work queued via
        /// Run/RunAsync AFTER this call has started completes with an
        /// ObjectDisposedException rather than silently vanishing.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _queue.CompleteAdding();
            _thread.Join();
            _queue.Dispose();
        }
    }
}