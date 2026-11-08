#nullable enable
using Godot;

namespace Dirge.Tool.Core;

/// <summary>
/// Per-user tool preferences stored in <see cref="EditorSettings"/> under <c>dirge/</c> (header sections, split offsets).
/// Outside the editor (overlay, tests) nothing is stored and reads report absent.
/// </summary>
public static class ToolSettings
{
    public const string HeaderSections = "dirge/header_sections";
    public const string BrowserSplit = "dirge/split_browser";
    public const string BottomSplit = "dirge/split_bottom";

    public static bool TryGet(string name, out Variant value)
    {
#if TOOLS
        if (Engine.IsEditorHint() && EditorInterface.Singleton.GetEditorSettings() is { } settings && settings.HasSetting(name))
        {
            value = settings.GetSetting(name);
            return true;
        }
#endif
        value = default;
        return false;
    }

    public static void Set(string name, Variant value)
    {
#if TOOLS
        if (Engine.IsEditorHint()) EditorInterface.Singleton.GetEditorSettings()?.SetSetting(name, value);
#endif
    }
}
