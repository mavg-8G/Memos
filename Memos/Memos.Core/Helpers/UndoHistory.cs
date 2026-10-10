using Memos.Core.Models;

namespace Memos.Core.Helpers;

/// <summary>
/// Undo/redo stack based on text replacements. Consecutive typing and consecutive deletions are
/// merged into one step, as users expect from a text editor.
/// </summary>
public sealed class UndoHistory
{
    private const int MaxEntries = 5000;
    private const long MaxStoredChars = 32L * 1024 * 1024;
    private static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(1);

    private readonly List<Entry> _undo = [];
    private readonly List<Entry> _redo = [];
    private readonly TimeProvider _time;
    private long _nextId = 1;
    private long _savedStateId;
    private long _storedChars;

    public UndoHistory(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    private sealed class Entry
    {
        public long Id;
        public int Offset;
        public string Removed = "";
        public string Inserted = "";
        public long Timestamp;
        public int Size => Removed.Length + Inserted.Length;
    }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>True when the current text is the one that was last marked as saved.</summary>
    public bool IsAtSavedState => CurrentStateId == _savedStateId;

    private long CurrentStateId => _undo.Count > 0 ? _undo[^1].Id : 0;

    public void MarkSaved() => _savedStateId = CurrentStateId;

    /// <summary>Makes the current state count as unsaved (for example after recovering a backup).</summary>
    public void MarkUnsaved() => _savedStateId = -1;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _storedChars = 0;
        _savedStateId = 0;
    }

    /// <param name="allowMerge">False for commands, which are always their own undo step.</param>
    public void Record(int offset, string removed, string inserted, bool allowMerge = true)
    {
        if (removed.Length == 0 && inserted.Length == 0)
        {
            return;
        }

        foreach (var entry in _redo)
        {
            _storedChars -= entry.Size;
        }

        _redo.Clear();

        var now = _time.GetTimestamp();
        if (allowMerge && _undo.Count > 0 && TryMerge(_undo[^1], offset, removed, inserted, now))
        {
            return;
        }

        _undo.Add(new Entry { Id = _nextId++, Offset = offset, Removed = removed, Inserted = inserted, Timestamp = now });
        _storedChars += removed.Length + inserted.Length;
        Trim();
    }

    /// <summary>Returns the edit that reverts the last step, or null if there is none.</summary>
    public TextEdit? Undo()
    {
        if (_undo.Count == 0)
        {
            return null;
        }

        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(entry);
        return new TextEdit(entry.Offset, entry.Inserted.Length, entry.Removed, entry.Offset + entry.Removed.Length, 0);
    }

    public TextEdit? Redo()
    {
        if (_redo.Count == 0)
        {
            return null;
        }

        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(entry);
        return new TextEdit(entry.Offset, entry.Removed.Length, entry.Inserted, entry.Offset + entry.Inserted.Length, 0);
    }

    private bool TryMerge(Entry last, int offset, string removed, string inserted, long now)
    {
        // Never grow the entry that represents the saved state: the document would look unmodified.
        if (last.Id == _savedStateId || _time.GetElapsedTime(last.Timestamp, now) > MergeWindow)
        {
            return false;
        }

        var before = last.Size;
        if (removed.Length == 0 && last.Removed.Length == 0)
        {
            // Typing forward. A new word starts a new step so undo goes word by word.
            if (offset != last.Offset + last.Inserted.Length || inserted.Contains('\n') || last.Inserted.EndsWith('\n'))
            {
                return false;
            }

            if (char.IsWhiteSpace(last.Inserted[^1]) && !char.IsWhiteSpace(inserted[0]))
            {
                return false;
            }

            last.Inserted += inserted;
        }
        else if (inserted.Length == 0 && last.Inserted.Length == 0)
        {
            if (removed.Contains('\n') || last.Removed.Contains('\n'))
            {
                return false;
            }

            if (offset + removed.Length == last.Offset)
            {
                last.Removed = removed + last.Removed; // Backspace
                last.Offset = offset;
            }
            else if (offset == last.Offset)
            {
                last.Removed += removed; // Delete
            }
            else
            {
                return false;
            }
        }
        else if (offset == last.Offset && removed == last.Inserted && removed.Length is > 0 and <= 64 && !inserted.Contains('\n'))
        {
            // The text just inserted is being replaced in place: an input method refining its composition.
            last.Inserted = inserted;
        }
        else
        {
            return false;
        }

        last.Timestamp = now;
        _storedChars += last.Size - before;
        return true;
    }

    private void Trim()
    {
        while (_undo.Count > 1 && (_undo.Count > MaxEntries || _storedChars > MaxStoredChars))
        {
            _storedChars -= _undo[0].Size;
            _undo.RemoveAt(0);
        }
    }
}
