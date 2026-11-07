#nullable enable
using System;
using Dirge.Serialization;
using Dirge.Tool.Core;
using Dirge.Tool.Shell;
using Godot;

namespace Dirge.Driver;

/// <summary>
/// Debug-build in-game overlay (autoload registered by the plugin): <see cref="ToggleAction"/> (default F9) shows the Mixer and
/// Profiler panes over the running game, bound to the running <see cref="DirgeServer"/> through <see cref="ServerAudition"/>.
/// Inert in release builds and under the headless display server.
/// </summary>
public partial class DirgeOverlay : Node
{
    public const string ToggleAction = "dirge_overlay";
    public const string ServerAutoloadPath = "/root/DirgeServer";

    private CanvasLayer? _layer;
    private TabContainer? _tabs;
    private Label? _status;
    private ProjectDocument? _document;
    private ServerAudition? _audition;
    private IToolPane? _mixer;
    private IToolPane? _profiler;
    private DirgeServer? _server;
    private bool _enabled;
    private double _statusTimer;

    public bool IsShown => _layer is { Visible: true };

    public override void _Ready()
    {
        _enabled = OS.IsDebugBuild() && !Engine.IsEditorHint() && DisplayServer.GetName() != "headless";
        if (!_enabled)
        {
            SetProcess(false);
            SetProcessUnhandledInput(false);
            return;
        }

        if (!InputMap.HasAction(ToggleAction))
        {
            InputMap.AddAction(ToggleAction);
            InputMap.ActionAddEvent(ToggleAction, new InputEventKey { PhysicalKeycode = Key.F9 });
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_enabled && @event.IsActionPressed(ToggleAction, allowEcho: false))
        {
            Toggle();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        if (_audition is null)
        {
            return;
        }

        _audition.Tick(delta);
        if (!IsShown)
        {
            return;
        }

        _statusTimer += delta;
        if (_statusTimer >= 0.1 && _status is not null)
        {
            _statusTimer = 0;
            Runtime.DirgeSystem? system = _audition.System;
            _status.Text = system is null
                ? "no project"
                : $"{system.InstanceCount} inst · {system.RealVoiceCount} voices · {_audition.LastUpdateMs:0.00} ms · {AudioServer.GetBusCount()} buses";
        }
    }

    public void Toggle()
    {
        if (_layer is null)
        {
            if (!Build())
            {
                return;
            }
        }

        _layer!.Visible = !_layer.Visible;
    }

    public void ShowOverlay()
    {
        if (_layer is not null || Build())
        {
            _layer!.Visible = true;
        }
    }

    public void HideOverlay()
    {
        if (_layer is not null)
        {
            _layer.Visible = false;
        }
    }

    private bool Build()
    {
        _server = FindServer();
        if (_server is null)
        {
            GD.PushWarning("Dirge overlay: no loaded DirgeServer in the scene tree.");
            return false;
        }

        _document = new ProjectDocument();
        _document.LoadFromText(ProjectJson.Serialize(_server.System.Project), _server.ProjectPath.Length > 0 ? _server.ProjectPath : null);
        _audition = new ServerAudition(_server, _document);
        var context = new ToolContext(_document, new Selection(), _audition, this);

        _layer = new CanvasLayer { Name = "DirgeOverlayLayer", Layer = 120, Visible = false };
        AddChild(_layer);

        var root = new Control { Name = "Root", MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(root);

        var panel = new PanelContainer { Name = "Panel" };
        panel.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        panel.AnchorTop = 0.5f;
        panel.OffsetTop = 0;
        panel.OffsetBottom = 0;
        panel.OffsetLeft = 0;
        panel.OffsetRight = 0;
        root.AddChild(panel);

        var column = new VBoxContainer { Name = "Column" };
        panel.AddChild(column);

        var header = new HBoxContainer { Name = "Header" };
        column.AddChild(header);
        header.AddChild(new Label { Text = "Dirge", SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin });
        _status = new Label { Text = "", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, HorizontalAlignment = HorizontalAlignment.Right };
        header.AddChild(_status);
        var close = new Button { Text = "Close (F9)" };
        close.Pressed += HideOverlay;
        header.AddChild(close);

        _tabs = new TabContainer { Name = "Tabs", SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        column.AddChild(_tabs);

        _mixer = PaneRegistry.Create(PaneRegistry.Mixer, "Mixer");
        _profiler = PaneRegistry.Create(PaneRegistry.Profiler, "Profiler");
        AddTab(_mixer);
        AddTab(_profiler);

        Attach(_mixer, context);
        Attach(_profiler, context);
        return true;
    }

    private void AddTab(IToolPane pane)
    {
        pane.Root.Name = pane.Title;
        var frame = new PaneFrame
        {
            Name = pane.Title + "Frame",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        frame.AddChild(pane.Root);
        _tabs!.AddChild(frame);
        _tabs.SetTabTitle(_tabs.GetTabCount() - 1, pane.Title);
    }

    private static void Attach(IToolPane pane, ToolContext context)
    {
        try
        {
            pane.Attach(context);
        }
        catch (Exception error)
        {
            GD.PushError($"Dirge overlay: pane '{pane.Title}' failed to attach: {error}");
        }
    }

    public override void _ExitTree()
    {
        _mixer?.Detach();
        _profiler?.Detach();
        _audition?.Dispose();
        _audition = null;
    }

    /// <summary>
    /// The loaded <see cref="DirgeServer"/> with the most instances (the one the game is actually using); ties go to the
    /// autoload at <see cref="ServerAutoloadPath"/>, then tree order.
    /// </summary>
    private DirgeServer? FindServer()
    {
        DirgeServer? best = GetNodeOrNull<DirgeServer>(ServerAutoloadPath) is { IsLoaded: true } autoload ? autoload : null;
        FindLoaded(GetTree().Root, ref best);
        return best;
    }

    private static void FindLoaded(Node node, ref DirgeServer? best)
    {
        if (node is DirgeServer { IsLoaded: true } server && (best is null || server.System.InstanceCount > best.System.InstanceCount))
        {
            best = server;
        }

        int count = node.GetChildCount();
        for (int i = 0; i < count; i++)
        {
            FindLoaded(node.GetChild(i), ref best);
        }
    }
}
