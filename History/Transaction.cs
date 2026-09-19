using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EditSharp.History
{
    /// <summary>
    /// One reversible step. The model produces these itself, at every
    /// primitive write — a property set, an entry placed in or taken out
    /// of a collection — while a Transaction is open, so an undo needs no
    /// knowledge of what operation the writes added up to. See
    /// Transaction's remarks for the whole design.
    /// </summary>
    public interface IChange
    {
        string Description { get; }
        void Undo();
        void Redo();
    }

    /// <summary>
    /// A property write, undone by writing the old value straight back
    /// into the backing field on the SAME object — never a copy, so every
    /// reference anything else holds to that object stays good across an
    /// undo. `write` is a static lambda over the owner so recording a
    /// change allocates nothing but this record.
    /// </summary>
    internal sealed class PropertyChange<TOwner, T>(TOwner owner, Action<TOwner, T> write, T oldValue, T newValue, string name) : IChange
        where TOwner : class
    {
        public string Description => $"{owner.GetType().Name}.{name}";
        public void Undo() => write(owner, oldValue);
        public void Redo() => write(owner, newValue);
    }

    /// <summary>A paired do/undo for anything that is not a plain property write — a collection entry, say.</summary>
    internal sealed class ActionChange(Action redo, Action undo, string description) : IChange
    {
        public string Description => description;
        public void Undo() => undo();
        public void Redo() => redo();
    }

    /// <summary>
    /// Everything one user action wrote, in the order it wrote it. Undone
    /// back to front, redone front to back — each step is self-contained,
    /// so replaying them in order reproduces the exact state either way.
    /// </summary>
    public sealed class ChangeSet(string description) : IChange
    {
        private readonly List<IChange> _steps = [];

        public string Description { get; } = description;
        public IReadOnlyList<IChange> Steps => _steps;
        public int Count => _steps.Count;

        internal void Add(IChange step) => _steps.Add(step);

        public void Undo()
        {
            for (int i = _steps.Count - 1; i >= 0; i--) _steps[i].Undo();
        }

        public void Redo()
        {
            foreach (IChange step in _steps) step.Redo();
        }
    }

    /// <summary>
    /// The ambient recorder every mutation in the model reports to.
    ///
    /// THE DESIGN: rather than an inverse written by hand for each editing
    /// operation — which would have to know every neighbour an extend
    /// overwrites, every fragment a split produces, every keyframe a trim
    /// shifts, and stay correct as those rules change — the model records
    /// each primitive write as it happens (Set for a property, Apply for a
    /// collection edit). Whoever runs a user action opens a transaction
    /// with a name, does the operation however it likes, and commits; the
    /// steps that landed in between become one History entry. Undo replays
    /// them in reverse on the same objects. Anything new the model does
    /// tomorrow is undoable the day it is written, provided it writes
    /// through Set/Apply like everything else.
    ///
    /// NO TRANSACTION OPEN: a write with a History.Active but no open
    /// transaction is not lost — it becomes an entry of its own, named
    /// after what was written. That is a safety net, not the intended
    /// path; a user action should always be wrapped, so it reads as one
    /// entry with one name. With no active history at all (building the
    /// initial project, tests) nothing is recorded.
    ///
    /// SUPPRESSED: constructing objects — a factory building a clip, a
    /// Duplicate deep-copying a graph — writes plenty of properties on
    /// things that are not part of the project yet. Those writes go
    /// through Suppress so they neither pollute a transaction nor fall
    /// into the safety net.
    ///
    /// THREADING: main thread only. Playback and rendering only ever read
    /// the model; nothing here is made safe for concurrent writers.
    /// </summary>
    public static class Transaction
    {
        private static ChangeSet? _current;
        private static History? _target;
        private static int _depth;
        private static bool _aborted;
        private static int _suppressed;
        private static bool _replaying;

        public static bool IsOpen => _current is not null;
        public static bool IsReplaying => _replaying;
        public static bool IsSuppressed => _suppressed > 0;

        /// <summary>
        /// Opens a transaction that commits into `history`, or joins the one
        /// already open — a nested Begin shares the outer entry and the
        /// outer description. Dispose the scope without Commit to roll
        /// back everything recorded since the outermost Begin: an operation
        /// that throws halfway leaves no half-applied entry behind.
        /// </summary>
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

        public sealed class Scope : IDisposable
        {
            private bool _committed;
            private bool _disposed;

            public void Commit() => _committed = true;

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

        /// <summary>Nothing written until the returned scope is disposed is recorded — see the class remarks, SUPPRESSED.</summary>
        public static IDisposable Suppress()
        {
            _suppressed++;
            return new Suppression();
        }

        /// <summary>Runs `make` with recording suppressed — the shape every factory and Duplicate wants.</summary>
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

        /// <summary>
        /// The property-setter helper. Writes `value` into `field` and, when
        /// something is listening, records how to put the old value back.
        /// `write` must assign the backing field directly — never the
        /// property — so replaying a change cannot re-enter recording.
        /// </summary>
        public static void Set<TOwner, T>(TOwner owner, ref T field, T value, Action<TOwner, T> write, [CallerMemberName] string name = "")
            where TOwner : class
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;

            T old = field;
            field = value;

            if (!Listening) return;

            Record(new PropertyChange<TOwner, T>(owner, write, old, value, name));
        }

        /// <summary>Performs `redo` now and records `undo` as its reverse — for collection edits and anything else that is not a property.</summary>
        public static void Apply(Action redo, Action undo, string description)
        {
            redo();

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

            // the safety net — see the class remarks, NO TRANSACTION OPEN
            EditSharpConfig.Logger.LogVerbose($"History: '{change.Description}' was written outside a transaction and became its own entry.");
            History.Active!.CommitStray(change);
        }

        /// <summary>Runs an undo or redo with recording switched off, so replaying writes does not record them again.</summary>
        internal static void Replay(Action action)
        {
            bool was = _replaying;
            _replaying = true;

            try { action(); }
            finally { _replaying = was; }
        }
    }
}
