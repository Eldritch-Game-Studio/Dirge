#nullable enable
using Godot;
using NecronomiDom.Runtime;
using HTMLElement = NecronomiDom.Dom.HTMLElement;

namespace Dirge.Tool.Core;

public static partial class ToolDocuments
{
    public const string FontPath = "res://addons/necronomidom/fonts/NotoSans-Regular.otf";
    public const string FontFamily = "Noto Sans";

    /// <summary>
    /// The document is parsed immediately so <c>host.Document</c> is bindable before the host enters the tree; layout and paint
    /// resources are created by the host itself on <c>_Ready</c>. <see cref="ToolScale.Current"/> is applied once the host has
    /// mounted (and again on every re-entry, because <see cref="DocumentHost"/> withdraws its text scale on exit) and whenever
    /// <see cref="ToolScale.Changed"/> fires while the host is in the tree. <see cref="ToolTheme.Current"/> is written as inline
    /// custom properties on the root element (<c>theme.css</c> holds the Dirge Dark defaults) at creation and on every
    /// <see cref="ToolTheme.Changed"/> while the host is in the tree.
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
        ApplyTheme(host);

        void Apply() => host.ApplyTextScale(ToolScale.Current);
        void Theme() => ApplyTheme(host);
        host.Ready += Apply;
        host.TreeEntered += () =>
        {
            ToolScale.Changed += Apply;
            ToolTheme.Changed += Theme;
            if (host.IsNodeReady()) Apply();
            Theme();
        };
        host.TreeExited += () =>
        {
            ToolScale.Changed -= Apply;
            ToolTheme.Changed -= Theme;
        };

        return host;
    }

    /// <summary>Writes the current palette as inline custom properties on the document root (one cascade invalidation).</summary>
    public static partial void ApplyTheme(DocumentHost host)
    {
        if (host.Document?.DocumentElement is HTMLElement root) root.Style.CssText = ToolTheme.Current.CssText;
    }

    /// <summary>
    /// A plain <see cref="Control"/> holding <paramref name="host"/> for mounting inside a container. Godot containers call
    /// <c>fit_child_in_rect</c> on their children, which resets the child's <c>scale</c> to 1 and forces its size to the cell;
    /// that undoes <see cref="DocumentHost.ApplyTextScale"/>. The container sizes the slot instead, and the host fits the slot
    /// at <c>slot.Size / TextScale</c>. <paramref name="minimumSize"/> is in authored pixels (see
    /// <see cref="SetAuthoredMinimumSize"/>).
    /// </summary>
    public static partial Control Slot(DocumentHost host, string name, Vector2 minimumSize)
    {
        var slot = new Control
        {
            Name = name,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            ClipContents = true,
        };
        SetAuthoredMinimumSize(slot, minimumSize);
        slot.AddChild(host);
        return slot;
    }

    /// <summary>
    /// Records <paramref name="authored"/> (scale-1 pixels) as the control's minimum size and applies it at the current scale.
    /// <see cref="ApplyMinimumSizes"/> re-applies it after a scale change.
    /// </summary>
    public static partial void SetAuthoredMinimumSize(Control control, Vector2 authored)
    {
        control.SetMeta(AuthoredMinimumMeta, authored);
        control.CustomMinimumSize = authored * ToolScale.Current;
    }

    /// <summary>Re-applies every authored minimum size under <paramref name="root"/> (inclusive) at the current scale.</summary>
    public static partial void ApplyMinimumSizes(Node root)
    {
        if (root is Control control && control.HasMeta(AuthoredMinimumMeta))
        {
            control.CustomMinimumSize = control.GetMeta(AuthoredMinimumMeta).AsVector2() * ToolScale.Current;
        }
        foreach (Node child in root.GetChildren())
        {
            if (child is not DocumentHost) ApplyMinimumSizes(child);
        }
    }
}
