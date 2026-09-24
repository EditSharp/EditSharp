using System;
using System.Threading;

namespace EditSharp.History
{
    /// <summary>
    /// Keeps playback threads from reading the model while it is mid-change.
    /// Every mutation (Transaction.Set, Transaction.Apply, undo/redo replay)
    /// holds the write side for that one change only. Session loops hold the
    /// read side just long enough to snapshot the structure they need for a
    /// tick (which clips are live, each graph's nodes and connections), then
    /// work from the snapshot. Parameter values are read without the lock.
    ///
    /// A thread holding the read side must not write to the model.
    /// </summary>
    internal static class ModelLock
    {
        private static readonly ReaderWriterLockSlim Lock = new(LockRecursionPolicy.SupportsRecursion);

        public static WriteScope Write()
        {
            Lock.EnterWriteLock();
            return default;
        }

        public static ReadScope Read()
        {
            Lock.EnterReadLock();
            return default;
        }

        public readonly struct WriteScope : IDisposable
        {
            public void Dispose() => Lock.ExitWriteLock();
        }

        public readonly struct ReadScope : IDisposable
        {
            public void Dispose() => Lock.ExitReadLock();
        }
    }
}
