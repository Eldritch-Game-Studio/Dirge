using System;

namespace Dirge.Project;

/// <summary>Stable identity for every authored object. Names may change; ids never do.</summary>
public readonly record struct DirgeId(Guid Value)
{
    public static DirgeId New() => new(Guid.NewGuid());
    public static readonly DirgeId None = default;
    public bool IsNone => Value == Guid.Empty;
    public override string ToString() => Value.ToString("N");
    public static DirgeId Parse(string text) => new(Guid.ParseExact(text, "N"));
}

/// <summary>Reference to an audio asset by engine path (e.g. <c>res://audio/wind.ogg</c>). The model never opens it.</summary>
public readonly record struct AssetRef(string Path)
{
    public bool IsEmpty => string.IsNullOrEmpty(Path);
    public override string ToString() => Path;
}
