using System;
using System.Collections.Generic;

namespace EditSharp.History
{
    /// <summary>What happened to a <see cref="History"/>, reported by <see cref="History.Changed"/>.</summary>
    public enum HistoryAction
    {
        /// <summary>A new entry was added.</summary>
        Commit,

        /// <summary>The last applied entry was reversed.</summary>
        Undo,

        /// <summary>The next undone entry was applied again.</summary>
        Redo,

        /// <summary>Every entry was dropped.</summary>
        Clear
    }

    /// <summary>Details of one <see cref="History.Changed"/> event.</summary>
    /// <param name="action">What happened.</param>
    /// <param name="entry">The entry committed, undone or redone; null for <see cref="HistoryAction.Clear"/>.</param>
    public sealed class HistoryEventArgs(HistoryAction action, ChangeSet? entry) : EventArgs
    {
        /// <summary>What happened.</summary>
        public HistoryAction Action { get; } = action;

        /// <summary>The entry committed, undone or redone; null for <see cref="HistoryAction.Clear"/>.</summary>
        public ChangeSet? Entry { get; } = entry;
    }

    /// <summary>A project's undo chain: one <see cref="ChangeSet"/> per user action, and a position in it.</summary>
    /// <remarks>
    /// Undo steps the position back and reverses the entry it passes; redo
    /// steps forward and reapplies it. Committing while redo entries remain
    /// drops them. Entries hold only the writes that were made (see
    /// <see cref="Transaction"/>), and <see cref="Limit"/> caps how many are kept.
    /// </remarks>
    public sealed class History
    {
        /// <summary>The history that writes made outside any transaction are recorded into; set when a project opens.</summary>
        public static History? Active { get; set; }

        private readonly List<ChangeSet> _chain = [];
        private int _position;

        /// <summary>How many entries are kept; the oldest are dropped past this.</summary>
        public int Limit { get; set; } = 1000;

        /// <summary>Every entry, oldest first, including undone ones still available to redo.</summary>
        public IReadOnlyList<ChangeSet> Entries => _chain;

        /// <summary>How many entries are applied. Entries before it can be undone; entries from it on can be redone.</summary>
        public int Position => _position;

        /// <summary>Whether there is an applied entry to undo.</summary>
        public bool CanUndo => _position > 0;

        /// <summary>Whether there is an undone entry to redo.</summary>
        public bool CanRedo => _position < _chain.Count;

        /// <summary>The description of the entry <see cref="Undo"/> would reverse; null when there is none.</summary>
        public string? UndoDescription => CanUndo ? _chain[_position - 1].Description : null;

        /// <summary>The description of the entry <see cref="Redo"/> would reapply; null when there is none.</summary>
        public string? RedoDescription => CanRedo ? _chain[_position].Description : null;

        /// <summary>Raised after an entry is committed, undone or redone, and after <see cref="Clear"/>.</summary>
        public event EventHandler<HistoryEventArgs>? Changed;

        /// <summary>Opens a transaction that commits into this history.</summary>
        /// <param name="description">The entry's name, shown for undo and redo.</param>
        /// <returns>The scope; call <see cref="Transaction.Scope.Commit"/> before disposing it to keep the writes.</returns>
        public Transaction.Scope Begin(string description) => Transaction.Begin(this, description);

        internal void Commit(ChangeSet entry)
        {
            //an action that wrote nothing isn't an entry; undoing it would appear to do nothing
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

        /// <summary>Reverses the last applied entry.</summary>
        /// <returns>False when there is nothing to undo, or a transaction is open.</returns>
        public bool Undo()
        {
            if (!CanUndo || Transaction.IsOpen) return false;

            ChangeSet entry = _chain[--_position];
            Transaction.Replay(entry.Undo);

            Changed?.Invoke(this, new HistoryEventArgs(HistoryAction.Undo, entry));
            return true;
        }

        /// <summary>Reapplies the next undone entry.</summary>
        /// <returns>False when there is nothing to redo, or a transaction is open.</returns>
        public bool Redo()
        {
            if (!CanRedo || Transaction.IsOpen) return false;

            ChangeSet entry = _chain[_position++];
            Transaction.Replay(entry.Redo);

            Changed?.Invoke(this, new HistoryEventArgs(HistoryAction.Redo, entry));
            return true;
        }

        /// <summary>Drops every entry. The model is left as it is.</summary>
        public void Clear()
        {
            _chain.Clear();
            _position = 0;

            Changed?.Invoke(this, new HistoryEventArgs(HistoryAction.Clear, null));
        }
    }
}
