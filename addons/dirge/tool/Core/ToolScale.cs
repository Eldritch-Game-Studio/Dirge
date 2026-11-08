#nullable enable
using System;
using Godot;

namespace Dirge.Tool.Core;

/// <summary>
/// The one UI scale every tool document and canvas follows. Inside the editor it is the editor display scale times the
/// main font size relative to Godot's 14 px default, unless the <c>dirge/ui_scale</c> editor setting is non-zero, in which
/// case that value is used verbatim. In a game (overlay) it is 1. <see cref="Changed"/> fires when the editor settings
/// change or the window moves to a screen with another DPI; the shell installs the watcher with <see cref="Watch"/>.
/// </summary>
public static partial class ToolScale
{
    public const string Setting = "dirge/ui_scale";
    /// <summary>Godot's default <c>interface/editor/main_font_size</c>; canvases and documents are authored against it.</summary>
    public const float BaseFontSize = 14f;

    private static float _current = 1f;
    private static bool _known;

    /// <summary>Current scale (1 = authored size). Computed on first use.</summary>
    public static float Current
    {
        get
        {
            if (!_known) Refresh();
            return _current;
        }
    }

    /// <summary>Raised after <see cref="Current"/> changed value.</summary>
    public static event Action? Changed;

    /// <summary>Recomputes <see cref="Current"/>; raises <see cref="Changed"/> when the value differs. Returns the new value.</summary>
    public static float Refresh()
    {
        float value = Compute();
        bool changed = _known && value != _current;
        _current = value;
        _known = true;
        if (changed) Changed?.Invoke();
        return value;
    }

    /// <summary>Rounds an authored (scale 1) pixel size to the current scale; never below 1 px.</summary>
    public static int Px(float authored) => Math.Max(1, Mathf.RoundToInt(authored * Current));

    private static float Compute()
    {
#if TOOLS
        if (Engine.IsEditorHint())
        {
            EditorSettings? settings = EditorInterface.Singleton.GetEditorSettings();
            if (settings is not null)
            {
                float @override = settings.HasSetting(Setting) ? settings.GetSetting(Setting).AsSingle() : 0f;
                if (@override > 0f) return @override;
                float fontSize = BaseFontSize;
                // 4.7 groups the setting under fonts/; earlier editors (the engine fork) keep the flat name.
                if (settings.HasSetting("interface/editor/fonts/main_font_size")) fontSize = settings.GetSetting("interface/editor/fonts/main_font_size").AsSingle();
                else if (settings.HasSetting("interface/editor/main_font_size")) fontSize = settings.GetSetting("interface/editor/main_font_size").AsSingle();
                if (fontSize <= 0f) fontSize = BaseFontSize;
                return EditorInterface.Singleton.GetEditorScale() * (fontSize / BaseFontSize);
            }
            return EditorInterface.Singleton.GetEditorScale();
        }
#endif
        return 1f;
    }

    /// <summary>
    /// Registers the <c>dirge/ui_scale</c> editor setting and adds a child node to <paramref name="owner"/> that refreshes the
    /// scale on editor-settings changes and DPI changes for as long as the owner is in the tree. No-op outside the editor.
    /// </summary>
    public static void Watch(Node owner)
    {
#if TOOLS
        if (!Engine.IsEditorHint()) return;
        EditorSettings? settings = EditorInterface.Singleton.GetEditorSettings();
        if (settings is not null && !settings.HasSetting(Setting))
        {
            settings.SetSetting(Setting, 0f);
            settings.SetInitialValue(Setting, 0f, false);
            settings.AddPropertyInfo(new Godot.Collections.Dictionary
            {
                ["name"] = Setting,
                ["type"] = (int)Variant.Type.Float,
                ["hint"] = (int)PropertyHint.Range,
                ["hint_string"] = "0,4,0.05,or_greater",
            });
        }
        owner.AddChild(new Watcher { Name = "ToolScaleWatcher" });
        Refresh();
#endif
    }

#if TOOLS
    /// <summary>Forwards editor-settings changes and DPI changes to <see cref="Refresh"/>.</summary>
    private sealed partial class Watcher : Node
    {
        private EditorSettings? _settings;

        public override void _EnterTree()
        {
            _settings = EditorInterface.Singleton.GetEditorSettings();
            if (_settings is not null) _settings.SettingsChanged += OnSettingsChanged;
        }

        public override void _ExitTree()
        {
            if (_settings is not null) _settings.SettingsChanged -= OnSettingsChanged;
            _settings = null;
        }

        public override void _Notification(int what)
        {
            if (what == NotificationWMDpiChange) Refresh();
        }

        private static void OnSettingsChanged() => Refresh();
    }
#endif
}
