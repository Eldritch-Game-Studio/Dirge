#nullable enable
using Godot;
using NecronomiDom.Runtime;

namespace Dirge.Tool.Core;

/// <summary>Creates NecronomiDOM hosts for tool documents under <c>res://addons/dirge/tool/ui/</c>. Implemented by the shell.</summary>
public static partial class ToolDocuments
{
    public const string UiRoot = "res://addons/dirge/tool/ui/";

    /// <summary>Host configured with the tool font, theme stylesheet and editor text scale; fills its parent Control.</summary>
    public static partial DocumentHost Create(string documentName);
}
