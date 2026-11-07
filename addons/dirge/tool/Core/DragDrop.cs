#nullable enable
using Dirge.Project;

namespace Dirge.Tool.Core;

/// <summary>Cross-pane drag payloads. Godot's Control drag API carries the Variant; use <see cref="ToolDrag"/> to wrap/unwrap.</summary>
public abstract record DragPayload;
public sealed record AssetDrag(string Path) : DragPayload;
public sealed record EventDrag(DirgeId Event) : DragPayload;
public sealed record SnapshotDrag(DirgeId Snapshot) : DragPayload;
public sealed record ParameterDrag(DirgeId Parameter, bool Global) : DragPayload;

public static partial class ToolDrag
{
    /// <summary>Wraps a payload for <c>Control.ForceDrag</c>/<c>_GetDragData</c>; the Variant is a <see cref="Godot.GodotObject"/> wrapper.</summary>
    public static Godot.Variant Wrap(DragPayload payload) => new DragHolder(payload);
    public static DragPayload? Unwrap(Godot.Variant data) => data.AsGodotObject() is DragHolder h ? h.Payload : null;

    private sealed partial class DragHolder : Godot.RefCounted
    {
        public DragPayload Payload { get; }
        public DragHolder(DragPayload payload) => Payload = payload;
    }
}
