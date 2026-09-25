using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EditSharp.History
{
    /// <summary>One reversible step, recorded by the model at each write while a transaction is open.</summary>
    public interface IChange
    {
        /// <summary>What was written, for display and logs.</summary>
        string Description { get; }

        /// <summary>Puts back what was there before.</summary>
        void Undo();

        /// <summary>Writes the change again.</summary>
        void Redo();
    }

    /// <summary>A property write, undone by writing the old value back into the same object.</summary>
    internal sealed class PropertyChange<TOwner, T>(TOwner owner, Action<TOwner, T> write, T oldValue, T newValue, string name) : IChange
        where TOwner : class
    {
        public string Description => $"{owner.GetType().Name}.{name}";
        public void Undo() => write(owner, oldValue);
        public void Redo() => write(owner, newValue);
    }

    /// <summary>A paired do and undo for anything that isn't a property write, such as a collection edit.</summary>
    internal sealed class ActionChange(Action redo, Action undo, string description) : IChange
    {
        public string Description => description;
        public void Undo() => undo();
        public void Redo() => redo();
    }

    /// <summary>Everything one user action wrote, in order: one <see cref="History"/> entry.</summary>
    /// <param name="description">The entry's name, shown for undo and redo.</param>
    public sealed class ChangeSet(string description) : IChange
    {
        private readonly List<IChange> _steps = [];

        /// <summary>The entry's name, shown for undo and redo.</summary>
        public string Description { get; } = description;

        /// <summary>The recorded writes, in the order they happened.</summary>
        public IReadOnlyList<IChange> Steps => _steps;

        /// <summary>How many writes were recorded.</summary>
        public int Count => _steps.Count;

        internal void Add(IChange step) => _steps.Add(step);

        /// <summary>Reverses every step, last first.</summary>
        public void Undo()
        {
            for (int i = _steps.Count - 1; i >= 0; i--) _steps[i].Undo();
        }

        /// <summary>Reapplies every step, first first.</summary>
        public void Redo()
        {
            foreach (IChange step in _steps) step.Redo();
        }
    }

    /// <summary>Records the model's writes so a user action can be undone as one entry.</summary>
    /// <remarks>
    /// The model reports each primitive write as it happens (<see cref="Set"/>
    /// for a property, <see cref="Apply"/> for anything else). A user action
    /// opens a transaction with <see cref="History.Begin"/>, makes its edits and
    /// commits; the writes in between become one entry, and undo replays them
    /// backwards on the same objects. A write with no transaction open becomes
    /// an entry of its own in <see cref="History.Active"/>, or goes unrecorded
    /// when there is no active history. Writes made while building objects
    /// that aren't in the project yet go through <see cref="Suppress"/>.
    /// Write from the main thread only.
    /// </remarks>
    public static class Transaction
    {
        private static ChangeSet? _current;
        private static History? _target;
        private static int _depth;
        private static bool _aborted;
        private static int _suppressed;
        private static bool _replaying;

        /// <summary>Whether a transaction is open.</summary>
        public static bool IsOpen => _current is not null;

        /// <summary>Whether an undo or redo is being replayed right now.</summary>
        public static bool IsReplaying => _replaying;

        /// <summary>Whether recording is switched off by <see cref="Suppress"/>.</summary>
        public static bool IsSuppressed => _suppressed > 0;

        /// <summary>Opens a transaction, or joins the one already open.</summary>
        /// <remarks>A nested call shares the outer entry and its description. Disposing any scope without committing it rolls back everything since the outermost call.</remarks>
        /// <param name="history">Where the entry is committed.</param>
        /// <param name="description">The entry's name, shown for undo and redo.</param>
        /// <returns>The scope; call <see cref="Scope.Commit"/> before disposing it to keep the writes.</returns>
        public static Scope Begin(History history, string description)
        {
            if (_current is null)
            {
                _current = new ChangeSet(description);
                _target = history;
                _aborted = false;
            }

            _depth++;
            return new Scope();
        }

        /// <summary>One opening of a transaction; the entry is committed when the outermost scope is disposed.</summary>
        public sealed class Scope : IDisposable
        {
            private bool _committed;
            private bool _disposed;

            /// <summary>Marks this scope's work as finished, so disposing it keeps the writes.</summary>
            public void Commit() => _committed = true;

            /// <summary>Closes the scope. The outermost scope commits the entry, or rolls it back if any scope wasn't committed.</summary>
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                if (!_committed) _aborted = true;

                if (--_depth > 0) return;

                ChangeSet set = _current!;
                History history = _target!;
                _current = null;
                _target = null;

                if (_aborted)
                {
                    _aborted = false;
                    Replay(set.Undo);
                    return;
                }

                history.Commit(set);
            }
        }

        /// <summary>Stops recording until the returned object is disposed.</summary>
        /// <returns>Dispose it to resume recording.</returns>
        public static IDisposable Suppress()
        {
            _suppressed++;
            return new Suppression();
        }

        /// <summary>Runs <paramref name="make"/> with recording off; for factories and Duplicate.</summary>
        /// <typeparam name="T">What <paramref name="make"/> builds.</typeparam>
        /// <param name="make">Builds the object.</param>
        /// <returns>What <paramref name="make"/> returned.</returns>
        public static T Suppressed<T>(Func<T> make)
        {
            using (Suppress()) return make();
        }

        private sealed class Suppression : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _suppressed--;
            }
        }

        /// <summary>Writes a property's backing field and records how to put the old value back.</summary>
        /// <typeparam name="TOwner">The object the property belongs to.</typeparam>
        /// <typeparam name="T">The property's type.</typeparam>
        /// <param name="owner">The object the property belongs to.</param>
        /// <param name="field">The backing field.</param>
        /// <param name="value">The new value. Nothing happens if it equals the current one.</param>
        /// <param name="write">Assigns the backing field on an owner. It must not call the property, so replaying can't record again.</param>
        /// <param name="name">The property's name, filled in by the compiler.</param>
        public static void Set<TOwner, T>(TOwner owner, ref T field, T value, Action<TOwner, T> write, [CallerMemberName] string name = "")
            where TOwner : class
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;

            T old = field;
            using (ModelLock.Write()) field = value;

            if (!Listening) return;

            Record(new PropertyChange<TOwner, T>(owner, write, old, value, name));
        }

        /// <summary>Runs <paramref name="redo"/> now and records <paramref name="undo"/> as its reverse; for collection edits and anything else that isn't a property.</summary>
        /// <param name="redo">Makes the change.</param>
        /// <param name="undo">Reverses it.</param>
        /// <param name="description">What was changed, for logs.</param>
        public static void Apply(Action redo, Action undo, string description)
        {
            using (ModelLock.Write()) redo();

            if (!Listening) return;

            Record(new ActionChange(redo, undo, description));
        }

        private static bool Listening => !_replaying && _suppressed == 0 && (_current is not null || History.Active is not null);

        private static void Record(IChange change)
        {
            if (_current is not null)
            {
                _current.Add(change);
                return;
            }

            //no transaction open: the write becomes an entry of its own
            EditSharpConfig.Logger.LogVerbose($"History: '{change.Description}' was written outside a transaction and became its own entry.");
            History.Active!.CommitStray(change);
        }

        //runs an undo or redo with recording off, so the replayed writes aren't recorded again
        internal static void Replay(Action action)
        {
            bool was = _replaying;
            _replaying = true;

            try
            {
                using (ModelLock.Write()) action();
            }
            finally { _replaying = was; }
        }
    }
}
