#nullable enable
using System;
using Dirge.Project;

namespace Dirge.Tool.Core;

public enum SelectionKind { None, Event, Instrument, Track, Marker, Bus, Effect, Vca, Snapshot, Parameter, Asset }

/// <summary>Tool-wide selection. One primary selection; panes react to <see cref="Changed"/>.</summary>
public sealed class Selection
{
    public SelectionKind Kind { get; private set; }
    /// <summary>Id of the selected object (event, instrument, bus, …); None for Asset.</summary>
    public DirgeId Id { get; private set; }
    /// <summary>Owning event for Instrument/Track/Marker/Parameter(local); owning bus for Effect.</summary>
    public DirgeId Owner { get; private set; }
    /// <summary>Effect index for Effect; asset path for Asset.</summary>
    public int Index { get; private set; } = -1;
    public string? AssetPath { get; private set; }

    public event Action? Changed;

    public void Select(SelectionKind kind, DirgeId id, DirgeId owner = default, int index = -1)
    {
        Kind = kind; Id = id; Owner = owner; Index = index; AssetPath = null;
        Changed?.Invoke();
    }

    public void SelectAsset(string path)
    {
        Kind = SelectionKind.Asset; Id = default; Owner = default; Index = -1; AssetPath = path;
        Changed?.Invoke();
    }

    public void Clear() => Select(SelectionKind.None, default);
}
