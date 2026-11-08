#if TOOLS
using Dirge.Tool.Shell;
using Godot;

namespace Dirge.Driver;

/// <summary>
/// Editor plugin: registers the <see cref="DirgeServer"/> and <see cref="DirgeOverlay"/> autoloads and adds the "Dirge" main
/// screen (<see cref="DirgeMainScreen"/>). The main screen restores the editor's bus layout when it leaves the tree.
/// <para><see cref="EditorPlugin.AddAutoloadSingleton"/> and <see cref="EditorPlugin.RemoveAutoloadSingleton"/> can save
/// <c>project.godot</c>, and a save normalizes the whole file (drops comments and default-valued settings, reorders keys,
/// rewrites autoload paths as <c>uid://</c>). The autoloads are therefore added only when missing and removed only when the
/// plugin is disabled, so opening or headlessly importing a project that already has them leaves its settings as authored.</para>
/// </summary>
[Tool]
public partial class DirgePlugin : EditorPlugin
{
    public const string AutoloadName = "DirgeServer";
    public const string AutoloadScript = "res://addons/dirge/runtime/DirgeServer.cs";
    public const string OverlayAutoloadName = "DirgeOverlay";
    public const string OverlayAutoloadScript = "res://addons/dirge/runtime/DirgeOverlay.cs";
    public const string IconPath = "res://addons/dirge/tool/ui/icons/dirge.svg";

    private DirgeMainScreen? _mainScreen;

    public DirgeMainScreen? MainScreen => _mainScreen;

    public override void _EnterTree()
    {
        if (!ProjectSettings.HasSetting(DirgeServer.ProjectPathSetting))
        {
            ProjectSettings.SetSetting(DirgeServer.ProjectPathSetting, "");
            ProjectSettings.SetInitialValue(DirgeServer.ProjectPathSetting, "");
            ProjectSettings.AddPropertyInfo(new Godot.Collections.Dictionary
            {
                ["name"] = DirgeServer.ProjectPathSetting,
                ["type"] = (int)Variant.Type.String,
                ["hint"] = (int)PropertyHint.File,
                ["hint_string"] = "*.json",
            });
        }

        RegisterAutoload(AutoloadName, AutoloadScript);
        RegisterAutoload(OverlayAutoloadName, OverlayAutoloadScript);

        _mainScreen = new DirgeMainScreen { Visible = false };
        EditorInterface.Singleton.GetEditorMainScreen().AddChild(_mainScreen);
    }

    public override void _ExitTree()
    {
        if (_mainScreen is not null)
        {
            // Detaching before free lets the main screen stop audition and restore the editor's audio bus layout.
            _mainScreen.GetParent()?.RemoveChild(_mainScreen);
            _mainScreen.QueueFree();
            _mainScreen = null;
        }
    }

    /// <summary>Only an explicit disable in Project Settings → Plugins removes the autoloads (editor exit keeps them).</summary>
    public override void _DisablePlugin()
    {
        RemoveAutoloadSingleton(OverlayAutoloadName);
        RemoveAutoloadSingleton(AutoloadName);
    }

    /// <summary>Whether <paramref name="name"/> still needs registering; an existing entry is left as authored.</summary>
    public static bool NeedsAutoload(string name) => !ProjectSettings.HasSetting("autoload/" + name);

    private void RegisterAutoload(string name, string script)
    {
        if (NeedsAutoload(name)) AddAutoloadSingleton(name, script);
    }

    public override bool _HasMainScreen() => true;

    public override string _GetPluginName() => "Dirge";

    public override Texture2D _GetPluginIcon()
    {
        if (ResourceLoader.Exists(IconPath) && ResourceLoader.Load<Texture2D>(IconPath) is { } icon)
        {
            return icon;
        }

        return EditorInterface.Singleton.GetEditorTheme().GetIcon("AudioStreamPlayer", "EditorIcons");
    }

    public override void _MakeVisible(bool visible)
    {
        if (_mainScreen is not null)
        {
            _mainScreen.Visible = visible;
        }
    }
}
#endif
