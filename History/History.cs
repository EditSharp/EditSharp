using System;
using System.Collections.Generic;

namespace EditSharp.History
{
    public enum HistoryAction
    {
        Commit,
        Undo,
        Redo,
        Clear
    }

    public sealed class HistoryEventArgs(HistoryAction action, ChangeSet? entry) : EventArgs
    {
        public HistoryAction Action { get; } = action;

        /// <summary>The entry committed, undone or redone — null for Clear.</summary>
        public ChangeSet? Entry { get; } = entry;
    }

    /// <summary>
    /// The chain of what the user has done, one ChangeSet per action, and
    /// a position in it. Undo steps the position back and reverses the
    /// entry it stepped over; redo steps forward and reapplies. A new
    /// entry committed while redo entries remain cuts them off first —
    /// once the chain forks, "redo" has no meaning any more.
    ///
    /// Entries hold only the writes that were made (see Transaction), so
    /// a long chain costs what the edits cost, never a copy of the
    /// project. Limit bounds it anyway, dropping the oldest entries.
    ///
    /// One History per project. Active names the one that adopts writes
    /// made outside any transaction — set it when the project is opened.
    /// </summary>
    public sealed class History
    {
        public static History? Active { get; set; }

        private readonly List<ChangeSet> _chain = [];
        private int _position;

        /// <summary>How many entries are kept before the oldest fall off the front.</summary>
        public int Limit { get; set; } = 1000;

        public IReadOnlyList<ChangeSet> Entries => _chain;

        /// <summary>The number of entries currently applied — everything before it is undoable, everything from it on is redoable.</summary>
        public int Position => _position;

        public bool CanUndo => _position > 0;
        public bool CanRedo => _position < _chain.Count;

        public string? UndoDescription => CanUndo ? _chain[_position - 1].Description : null;
        public string? RedoDescription => CanRedo ? _chain[_position].Description : null;

        /// <summary>Raised after an entry is committed, undone or redone, or the chain cleared — the moment a view should re-read the model.</summary>
        public event EventHandler<HistoryEventArgs>? Changed;

        /// <summary>Open a transaction that commits here — see Transaction.Begin.</summary>
        public Transaction.Scope Begin(string description) => Transaction.Begin(this, description);

        internal void Commit(ChangeSet entry)
        {
            // an action that wrote nothing is not an entry — undoing it
            // would do nothing, which reads as undo being broken
            if (entry.Count == 0) return;

            if (_position < _chain.Count) _chain.RemoveRange(_position, _chain.Count - _position);

            _chain.Add(entry);
            _position = _chain.Count;

            while (_chain.Count > Limit && Limit > 0)
            {
                _chain.RemoveAt(0);
                _position--;
            }

            Changed?.Invoke(this, new HistoryEventArgs(HistoryAction.Commit, entry));
        }

        internal void CommitStray(IChange change)
        {
            var entry = new ChangeSet(change.Description);
            entry.Add(change);
            Commit(entry);
        }

        /// <summary>Reverses the last applied entry. False if there is none, or a transaction is mid-flight — its writes are not an entry yet.</summary>
        public bool Undo()
        {
            if (!CanUndo || Transaction.IsOpen) return false;

            ChangeSet entry = _chain[--_position];
            Transaction.Replay(entry.Undo);

            Changed?.Invoke(this, new HistoryEventArgs(HistoryAction.Undo, entry));
            return true;
        }

        public bool Redo()
        {
            if (!CanRedo || Transaction.IsOpen) return false;

            ChangeSet entry = _chain[_position++];
            Transaction.Replay(entry.Redo);

            Changed?.Invoke(this, new HistoryEventArgs(HistoryAction.Redo, entry));
            return true;
        }

        /// <summary>Forgets the whole chain. The model is left as it is — this only drops the ability to go back.</summary>
        public void Clear()
        {
            _chain.Clear();
            _position = 0;

            Changed?.Invoke(this, new HistoryEventArgs(HistoryAction.Clear, null));
        }
    }
}
