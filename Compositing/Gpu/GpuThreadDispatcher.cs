using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace EditSharp.Compositing.Gpu
{
    /// <summary>Runs work on one dedicated thread, in order, so a GPU context is created, used and destroyed by the same thread.</summary>
    /// <remarks>Skia's GRContext isn't documented as safe to use from different threads in turn, which is what continuing after an await does. Pair one dispatcher with the GPU resources it protects; it isn't a general thread pool.</remarks>
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
            //blocks until work arrives or Dispose completes the queue; each action runs here, in order
            foreach (Action action in _queue.GetConsumingEnumerable())
            {
                action();
            }
        }

        //runs `action` on the dispatcher's thread; completes when it does, or with its exception
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

        //runs `func` on the dispatcher's thread and hands back its result
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
                //Add throws once Dispose has completed the queue: treated as the dispatcher being gone
                _queue.Add(wrapped);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        //stops taking work, finishes what's queued and joins the thread; work queued after that fails with ObjectDisposedException
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