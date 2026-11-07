#nullable enable
using Godot;
using NecronomiDom.Runtime;

namespace Dirge.Tool.Core;

public static partial class ToolDocuments
{
    public const string FontPath = "res://addons/necronomidom/fonts/NotoSans-Regular.otf";
    public const string FontFamily = "Noto Sans";

    /// <summary>
    /// The document is parsed immediately so <c>host.Document</c> is bindable before the host enters the tree; layout and paint
    /// resources are created by the host itself on <c>_Ready</c>. The editor text scale is applied once the host has mounted
    /// (and again on every re-entry), because <see cref="DocumentHost.ApplyTextScale"/> needs the mounted authored scale.
    /// </summary>
    public static partial DocumentHost Create(string documentName)
    {
        var host = new DocumentHost
        {
            Name = documentName.Replace('.', '_'),
            DocumentPath = UiRoot + documentName,
            FontPath = FontPath,
            FontFamily = FontFamily,
            DeveloperReloadEnabled = false,
            DeveloperReloadWatchEnabled = false,
        };
        // No anchors: the host sizes itself to its parent (divided by the text scale) and anchors would make Godot warn
        // about overriding the size of a node with non-equal opposite anchors.
        host.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        host.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        host.LoadDocument(host.DocumentPath);

        float scale = EditorScale();
        if (scale != 1f)
        {
            host.Ready += () => host.ApplyTextScale(scale);
            host.TreeEntered += () =>
            {
                if (host.IsNodeReady())
                {
                    host.ApplyTextScale(scale);
                }
            };
        }

        return host;
    }

    /// <summary>Editor UI scale when running inside the editor; 1 in a game (overlay).</summary>
    public static float EditorScale()
    {
#if TOOLS
        if (Engine.IsEditorHint())
        {
            return EditorInterface.Singleton.GetEditorScale();
        }
#endif
        return 1f;
    }
}
