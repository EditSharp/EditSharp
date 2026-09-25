using System;
using System.Threading;

namespace EditSharp.History
{
    /// <summary>Keeps playback threads from reading the model while it's mid-change.</summary>
    /// <remarks>
    /// Every write (Transaction.Set, Transaction.Apply, undo and redo) holds the
    /// write side for that one change. Playback and render loops hold the read
    /// side only while they snapshot what a tick needs (the live clips, each
    /// graph's nodes and connections), then work from the snapshot. Parameter
    /// values are read without the lock. A thread holding the read side must
    /// not write to the model.
    /// </remarks>
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
