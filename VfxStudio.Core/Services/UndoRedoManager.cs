using System;
using System.Collections.Generic;

namespace VfxStudio.Core.Services;

/// <summary>
/// Interface for an undoable and redoable command.
/// </summary>
public interface IUndoableCommand
{
    /// <summary>
    /// User-friendly name or description of the action (e.g. "Add VFX Slot", "Modify Scale").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Executes the action.
    /// </summary>
    void Execute();

    /// <summary>
    /// Undoes the action, restoring prior state.
    /// </summary>
    void Undo();

    /// <summary>
    /// Redoes the action.
    /// </summary>
    void Redo();
}

/// <summary>
/// A delegate-based undoable command.
/// </summary>
public class RelayUndoableCommand : IUndoableCommand
{
    private readonly Action _execute;
    private readonly Action _undo;
    private readonly Action? _redo;

    public string Name { get; }

    public RelayUndoableCommand(string name, Action execute, Action undo, Action? redo = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _undo = undo ?? throw new ArgumentNullException(nameof(undo));
        _redo = redo;
    }

    public void Execute() => _execute();
    public void Undo() => _undo();
    public void Redo() => (_redo ?? _execute)();
}

/// <summary>
/// A composite command consisting of multiple sequential sub-commands.
/// </summary>
public class CompositeCommand : IUndoableCommand
{
    private readonly List<IUndoableCommand> _commands = new();

    public string Name { get; }
    public IReadOnlyList<IUndoableCommand> Commands => _commands;

    public CompositeCommand(string name, IEnumerable<IUndoableCommand>? commands = null)
    {
        Name = name;
        if (commands != null)
        {
            _commands.AddRange(commands);
        }
    }

    public void Add(IUndoableCommand command)
    {
        _commands.Add(command);
    }

    public void Execute()
    {
        foreach (var cmd in _commands)
        {
            cmd.Execute();
        }
    }

    public void Undo()
    {
        // Undo in reverse order
        for (int i = _commands.Count - 1; i >= 0; i--)
        {
            _commands[i].Undo();
        }
    }

    public void Redo()
    {
        foreach (var cmd in _commands)
        {
            cmd.Redo();
        }
    }
}

/// <summary>
/// Lightweight undo/redo command stack for motion slots and EFX attribute edits.
/// </summary>
public class UndoRedoManager
{
    private readonly Stack<IUndoableCommand> _undoStack = new();
    private readonly Stack<IUndoableCommand> _redoStack = new();
    private int _maxHistory = 200;

    /// <summary>
    /// Event raised when undo/redo stacks change.
    /// </summary>
    public event EventHandler? HistoryChanged;

    /// <summary>
    /// Maximum number of undoable commands to retain. Set &lt;= 0 for unlimited.
    /// </summary>
    public int MaxHistory
    {
        get => _maxHistory;
        set => _maxHistory = value;
    }

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public int UndoCount => _undoStack.Count;
    public int RedoCount => _redoStack.Count;

    public string? NextUndoName => CanUndo ? _undoStack.Peek().Name : null;
    public string? NextRedoName => CanRedo ? _redoStack.Peek().Name : null;

    /// <summary>
    /// Executes a command and pushes it onto the undo stack, clearing the redo stack.
    /// </summary>
    public void Execute(IUndoableCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        command.Execute();
        _undoStack.Push(command);
        _redoStack.Clear();

        TrimHistory();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Records a command that has already been executed into the undo stack without re-executing it.
    /// </summary>
    public void Record(IUndoableCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        _undoStack.Push(command);
        _redoStack.Clear();

        TrimHistory();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Undoes the last command.
    /// </summary>
    /// <returns>True if undone, false if undo stack was empty.</returns>
    public bool Undo()
    {
        if (_undoStack.Count == 0) return false;

        var command = _undoStack.Pop();
        try
        {
            command.Undo();
        }
        catch
        {
            // Re-push on error so state isn't lost
            _undoStack.Push(command);
            throw;
        }

        _redoStack.Push(command);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Redoes the last undone command.
    /// </summary>
    /// <returns>True if redone, false if redo stack was empty.</returns>
    public bool Redo()
    {
        if (_redoStack.Count == 0) return false;

        var command = _redoStack.Pop();
        try
        {
            command.Redo();
        }
        catch
        {
            _redoStack.Push(command);
            throw;
        }

        _undoStack.Push(command);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Clears both undo and redo stacks.
    /// </summary>
    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TrimHistory()
    {
        if (_maxHistory <= 0 || _undoStack.Count <= _maxHistory) return;

        // Trim oldest items by reconstructing stack
        var items = _undoStack.ToArray(); // [0] is top, [N-1] is bottom
        _undoStack.Clear();
        for (int i = _maxHistory - 1; i >= 0; i--)
        {
            _undoStack.Push(items[i]);
        }
    }
}
