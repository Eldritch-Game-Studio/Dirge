#nullable enable
using Godot;
using NecronomiDom.Runtime;

namespace Dirge.Tool.Core;

/// <summary>Creates NecronomiDOM hosts for tool documents under <c>res://addons/dirge/tool/ui/</c> and mounts them at
/// <see cref="ToolScale.Current"/>. Implemented by the shell.</summary>
public static partial class ToolDocuments
{
    public const string UiRoot = "res://addons/dirge/tool/ui/";
    /// <summary>Metadata key holding a control's authored (scale-1) minimum size as a <see cref="Vector2"/>.</summary>
    public const string AuthoredMinimumMeta = "dirge_authored_minimum";

    /// <summary>Host configured with the tool font, theme stylesheet and tool text scale; fills its parent Control.</summary>
    public static partial DocumentHost Create(string documentName);

    /// <summary>Plain Control wrapping <paramref name="host"/> so a Godot container never resets the host's scale.</summary>
    public static partial Control Slot(DocumentHost host, string name, Vector2 minimumSize);

    /// <summary>Sets a minimum size authored at scale 1; <see cref="ApplyMinimumSizes"/> rescales it after a scale change.</summary>
    public static partial void SetAuthoredMinimumSize(Control control, Vector2 authored);

    /// <summary>Re-applies authored minimum sizes in <paramref name="root"/>'s subtree at the current scale.</summary>
    public static partial void ApplyMinimumSizes(Node root);
}
