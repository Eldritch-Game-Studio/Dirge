#if TOOLS
using Godot;

namespace Dirge.Driver;

/// <summary>Editor plugin: registers the <see cref="DirgeServer"/> autoload while enabled.</summary>
[Tool]
public partial class DirgePlugin : EditorPlugin
{
    public const string AutoloadName = "DirgeServer";
    public const string AutoloadScript = "res://addons/dirge/runtime/DirgeServer.cs";

    public override void _EnterTree()
    {
        AddAutoloadSingleton(AutoloadName, AutoloadScript);
    }

    public override void _ExitTree()
    {
        RemoveAutoloadSingleton(AutoloadName);
    }
}
#endif
