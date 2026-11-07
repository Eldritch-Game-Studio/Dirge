#nullable enable
using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Serialization;
using Dirge.Validation;

namespace Dirge.Tool.Core;

/// <summary>What part of the project a change touched; panes refresh only what they show.</summary>
[Flags]
public enum ChangeScope
{
    None = 0,
    Mixer = 1 << 0,        // buses, effects, VCAs (structure or values)
    Snapshots = 1 << 1,
    GlobalParameters = 1 << 2,
    Events = 1 << 3,       // event list / paths / banks
    EventContent = 1 << 4, // tracks, instruments, markers, automation of one event (see ChangedEvent)
    Assets = 1 << 5,
    All = ~0,
}

/// <summary>Editable project with undo/redo and change notification. The only writer of <see cref="Project"/> in the tool.</summary>
public sealed class ProjectDocument
{
    private readonly UndoStack _undo = new();

    public AudioProject Project { get; private set; } = NewProject();
    /// <summary>Absolute or res:// path; null when never saved.</summary>
    public string? Path { get; private set; }
    public bool IsDirty { get; private set; }
    public UndoStack Undo => _undo;

    /// <summary>Raised after every mutation, undo and redo, and after Load/New (scope All).</summary>
    public event Action<ChangeScope, DirgeId>? Changed;

    /// <summary>Apply a reversible edit. <paramref name="apply"/> runs immediately; both delegates must be idempotent for their direction.</summary>
    public void Mutate(string label, ChangeScope scope, Action apply, Action revert, DirgeId subject = default)
    {
        apply();
        _undo.Push(new UndoEntry(label, scope, subject, apply, revert));
        MarkChanged(scope, subject);
    }

    /// <summary>Coalesce continuous edits (fader drags) into one undo entry: call with the same <paramref name="token"/> while dragging.</summary>
    public void MutateContinuous(object token, string label, ChangeScope scope, Action apply, Action revert, DirgeId subject = default)
    {
        apply();
        _undo.PushOrMerge(token, new UndoEntry(label, scope, subject, apply, revert));
        MarkChanged(scope, subject);
    }

    public bool CanUndo => _undo.CanUndo;
    public bool CanRedo => _undo.CanRedo;
    public void UndoOne() { if (_undo.TryUndo(out UndoEntry e)) MarkChanged(e.Scope, e.Subject); }
    public void RedoOne() { if (_undo.TryRedo(out UndoEntry e)) MarkChanged(e.Scope, e.Subject); }

    public void New()
    {
        Project = NewProject();
        Path = null;
        _undo.Clear();
        IsDirty = false;
        Changed?.Invoke(ChangeScope.All, default);
    }

    public void LoadFromText(string json, string? path)
    {
        Project = ProjectJson.Deserialize(json);
        Path = path;
        _undo.Clear();
        IsDirty = false;
        Changed?.Invoke(ChangeScope.All, default);
    }

    public string ToText() => ProjectJson.Serialize(Project);

    /// <summary>Caller writes the text; this records the path and clears dirty.</summary>
    public void MarkSaved(string path)
    {
        Path = path;
        IsDirty = false;
    }

    public IReadOnlyList<Diagnostic> Validate() => ProjectValidator.Validate(Project);

    private void MarkChanged(ChangeScope scope, DirgeId subject)
    {
        IsDirty = true;
        Changed?.Invoke(scope, subject);
    }

    public static AudioProject NewProject()
    {
        var project = new AudioProject { Name = "Untitled" };
        project.Mixer.Buses.Add(new Bus { Name = "Master" });
        return project;
    }
}

public readonly record struct UndoEntry(string Label, ChangeScope Scope, DirgeId Subject, Action Apply, Action Revert);

public sealed class UndoStack
{
    private readonly List<UndoEntry> _done = new();
    private readonly List<UndoEntry> _undone = new();
    private object? _mergeToken;

    public bool CanUndo => _done.Count > 0;
    public bool CanRedo => _undone.Count > 0;
    public string? UndoLabel => _done.Count > 0 ? _done[^1].Label : null;
    public string? RedoLabel => _undone.Count > 0 ? _undone[^1].Label : null;

    public void Push(UndoEntry entry)
    {
        _done.Add(entry);
        _undone.Clear();
        _mergeToken = null;
    }

    /// <summary>Replace the top entry's Apply (keeping the original Revert) when the token matches the last push.</summary>
    public void PushOrMerge(object token, UndoEntry entry)
    {
        if (ReferenceEquals(_mergeToken, token) && _done.Count > 0)
        {
            UndoEntry top = _done[^1];
            _done[^1] = top with { Apply = entry.Apply };
            return;
        }
        Push(entry);
        _mergeToken = token;
    }

    /// <summary>End a continuous edit so the next push with the same token starts a new entry.</summary>
    public void EndContinuous() => _mergeToken = null;

    public bool TryUndo(out UndoEntry entry)
    {
        if (_done.Count == 0) { entry = default; return false; }
        entry = _done[^1];
        _done.RemoveAt(_done.Count - 1);
        entry.Revert();
        _undone.Add(entry);
        _mergeToken = null;
        return true;
    }

    public bool TryRedo(out UndoEntry entry)
    {
        if (_undone.Count == 0) { entry = default; return false; }
        entry = _undone[^1];
        _undone.RemoveAt(_undone.Count - 1);
        entry.Apply();
        _done.Add(entry);
        return true;
    }

    public void Clear() { _done.Clear(); _undone.Clear(); _mergeToken = null; }
}
