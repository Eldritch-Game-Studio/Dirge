#nullable enable
#if TOOLS
using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Tool.Core;
using Godot;
using NecronomiDom.Dom;
using NecronomiDom.Runtime;

namespace Dirge.Tool.Shell;

/// <summary>
/// The "Dirge" editor main screen: menu bar and status bar (NecronomiDOM documents), Browser | (Transport / Events) over
/// (Mixer | Profiler | Validation) tabs. Owns the <see cref="ToolContext"/>, the <see cref="EditorAudition"/> and file operations.
/// Panes are resolved by type name through <see cref="PaneRegistry"/>.
/// </summary>
[Tool]
public partial class DirgeMainScreen : Control
{
    private const string LastProjectSetting = "dirge/last_project";
    private const double StatusRefreshSeconds = 0.1;

    private readonly ProjectDocument _document = new();
    private readonly Selection _selection = new();
    private readonly List<IToolPane> _panes = new();
    private EditorAudition _audition = null!;
    private ToolContext _context = null!;
    private LiveUpdateBridge _live = null!;

    private DocumentHost _menu = null!;
    private DocumentHost _status = null!;
    private TabContainer _bottomTabs = null!;
    private ValidationPanel _validation = null!;
    private IToolPane _browser = null!;
    private IToolPane _transport = null!;
    private IToolPane _events = null!;
    private IToolPane _mixer = null!;
    private IToolPane _profiler = null!;

    private Element _undoButton = null!;
    private Element _redoButton = null!;
    private Element _connectButton = null!;
    private HTMLElement _liveState = null!;
    private Element _projectName = null!;
    private Element _statusDirty = null!;
    private Element _statusPath = null!;
    private Element _statusErrors = null!;
    private Element _statusWarnings = null!;
    private Element _statusInstances = null!;
    private Element _statusVoices = null!;
    private Element _statusMs = null!;
    private Element _statusBuses = null!;
    private Element _statusLive = null!;
    private Element _statusMessage = null!;

    private double _statusTimer;
    private string _message = "";
    private bool _messageIsError;
    private double _messageTtl;
    private string _lastInstances = "", _lastVoices = "", _lastMs = "", _lastBuses = "", _lastLive = "";

    public ProjectDocument Document => _document;
    public Selection Selection => _selection;
    public EditorAudition Audition => _audition;
    public ToolContext Context => _context;
    public ValidationPanel Validation => _validation;
    public IReadOnlyList<IToolPane> Panes => _panes;
    /// <summary>Editor-side live update (also reachable by panes as the <c>LiveUpdateClient</c> child of <see cref="ToolContext.Host"/>).</summary>
    public LiveUpdateBridge Live => _live;

    public override void _Ready()
    {
        Name = "Dirge";
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        FocusMode = FocusModeEnum.All;

        _audition = new EditorAudition(this, _document);
        _context = new ToolContext(_document, _selection, _audition, this);
        _live = new LiveUpdateBridge(this, _document);

        BuildLayout();
        BindMenu();
        BindStatus();
        CreateDialogs();

        _document.Changed += OnDocumentChanged;
        _validation.CountsChanged += OnValidationCounts;
        _live.StateChanged += OnLiveStateChanged;

        foreach (IToolPane pane in _panes)
        {
            try
            {
                pane.Attach(_context);
            }
            catch (Exception error)
            {
                GD.PushError($"Dirge: pane '{pane.Title}' failed to attach: {error}");
            }
        }

        RefreshDocumentUi();
        RestoreLastProject();
    }

    public override void _ExitTree()
    {
        foreach (IToolPane pane in _panes)
        {
            try
            {
                pane.Detach();
            }
            catch (Exception error)
            {
                GD.PushError($"Dirge: pane '{pane.Title}' failed to detach: {error}");
            }
        }

        _document.Changed -= OnDocumentChanged;
        _audition.Dispose();
        _live.Dispose();
    }

    public override void _Process(double delta)
    {
        _audition.Tick(delta);
        _live.Tick(delta);
        _statusTimer += delta;
        if (_messageTtl > 0)
        {
            _messageTtl -= delta;
            if (_messageTtl <= 0)
            {
                SetMessage("", false);
            }
        }

        if (_statusTimer >= StatusRefreshSeconds)
        {
            _statusTimer = 0;
            RefreshLiveStatus();
        }
    }

    // ---- layout -----------------------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        float scale = ToolDocuments.EditorScale();
        var layout = new VBoxContainer { Name = "Layout" };
        layout.SetAnchorsPreset(LayoutPreset.FullRect);
        layout.AddThemeConstantOverride("separation", 0);
        AddChild(layout);

        var menuBar = new Control { Name = "MenuBar", CustomMinimumSize = new Vector2(0, 28 * scale), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _menu = ToolDocuments.Create("shell.html");
        menuBar.AddChild(_menu);
        layout.AddChild(menuBar);

        // SplitContainer measures its offset from the non-expanding child's minimum size, so the browser (left) and the
        // bottom tabs are Fill-only with a minimum size; the center/top areas take the remaining space. Every pane sits in a
        // PaneFrame whose minimum size is the shell's, not the pane's.
        var main = new HSplitContainer { Name = "Main", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        layout.AddChild(main);

        _browser = AddPane(PaneRegistry.Browser, "Browser");
        PaneFrame browserFrame = Frame(_browser, new Vector2(260 * scale, 0));
        browserFrame.SizeFlagsHorizontal = SizeFlags.Fill;
        main.AddChild(browserFrame);

        var center = new VSplitContainer { Name = "Center", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        main.AddChild(center);

        // Transport docked above the event editor; the transport keeps a fixed (draggable) height, the editor takes the rest.
        var top = new VSplitContainer { Name = "Top", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        center.AddChild(top);

        _transport = AddPane(PaneRegistry.Transport, "Transport");
        float transportHeight = Mathf.Clamp(_transport.Root.GetCombinedMinimumSize().Y, 136 * scale, 200 * scale);
        PaneFrame transportFrame = Frame(_transport, new Vector2(0, transportHeight));
        transportFrame.SizeFlagsVertical = SizeFlags.Fill;
        top.AddChild(transportFrame);

        _events = AddPane(PaneRegistry.Events, "Events");
        top.AddChild(Frame(_events, new Vector2(0, 120 * scale)));

        _bottomTabs = new TabContainer { Name = "Bottom", SizeFlagsVertical = SizeFlags.Fill, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 280 * scale), TabAlignment = TabBar.AlignmentMode.Left };
        center.AddChild(_bottomTabs);

        _mixer = AddPane(PaneRegistry.Mixer, "Mixer");
        _profiler = AddPane(PaneRegistry.Profiler, "Profiler");
        _validation = new ValidationPanel();
        _panes.Add(_validation);
        AddTab(_mixer);
        AddTab(_profiler);
        AddTab(_validation);

        var statusBar = new Control { Name = "StatusBar", CustomMinimumSize = new Vector2(0, 22 * scale), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _status = ToolDocuments.Create("shell-status.html");
        statusBar.AddChild(_status);
        layout.AddChild(statusBar);
    }

    private IToolPane AddPane(string typeName, string title)
    {
        IToolPane pane = PaneRegistry.Create(typeName, title);
        pane.Root.Name = title;
        _panes.Add(pane);
        return pane;
    }

    private static PaneFrame Frame(IToolPane pane, Vector2 minimum)
    {
        var frame = new PaneFrame
        {
            Name = pane.Title + "Frame",
            FrameMinimumSize = minimum,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        frame.AddChild(pane.Root);
        return frame;
    }

    private void AddTab(IToolPane pane)
    {
        PaneFrame frame = Frame(pane, Vector2.Zero);
        _bottomTabs.AddChild(frame);
        _bottomTabs.SetTabTitle(_bottomTabs.GetTabCount() - 1, pane.Title);
    }

    /// <summary>Shows the bottom tab holding <paramref name="pane"/>.</summary>
    public void ShowTab(IToolPane pane)
    {
        int index = pane.Root.GetParent() is PaneFrame frame ? _bottomTabs.GetTabIdxFromControl(frame) : -1;
        if (index >= 0)
        {
            _bottomTabs.CurrentTab = index;
        }
    }

    /// <summary>The pane whose subtree holds the GUI focus owner; null when focus is outside every pane.</summary>
    public IToolPane? FocusedPane()
    {
        Control? focus = GetViewport()?.GuiGetFocusOwner();
        for (Godot.Node? node = focus; node is not null && node != this; node = node.GetParent())
        {
            foreach (IToolPane pane in _panes)
            {
                if (pane.Root == node)
                {
                    return pane;
                }
            }
        }

        return null;
    }

    // ---- menu / status documents ---------------------------------------------------------------------------------------

    private void BindMenu()
    {
        Document document = _menu.Document;
        Listen(document, "file-new", NewProject);
        Listen(document, "file-open", OpenProject);
        Listen(document, "file-save", () => SaveProject(saveAs: false));
        Listen(document, "file-save-as", () => SaveProject(saveAs: true));
        Listen(document, "edit-undo", Undo);
        Listen(document, "edit-redo", Redo);
        Listen(document, "project-validate", ValidateProject);
        Listen(document, "project-build-banks", BuildBanks);
        Listen(document, "live-connect", ToggleLiveConnection);
        _undoButton = Required(document, "edit-undo");
        _redoButton = Required(document, "edit-redo");
        _connectButton = Required(document, "live-connect");
        _liveState = (HTMLElement)Required(document, "live-state");
        _projectName = Required(document, "project-name");
    }

    private void BindStatus()
    {
        Document document = _status.Document;
        _statusDirty = Required(document, "status-dirty");
        _statusPath = Required(document, "status-path");
        _statusErrors = Required(document, "status-errors");
        _statusWarnings = Required(document, "status-warnings");
        _statusInstances = Required(document, "status-instances");
        _statusVoices = Required(document, "status-voices");
        _statusMs = Required(document, "status-ms");
        _statusBuses = Required(document, "status-buses");
        _statusLive = Required(document, "status-live");
        _statusMessage = Required(document, "status-message");
    }

    private void OnDocumentChanged(ChangeScope scope, DirgeId subject) => RefreshDocumentUi();

    /// <summary>Undo/redo labels, dirty flag, path and project name.</summary>
    private void RefreshDocumentUi()
    {
        UndoStack undo = _document.Undo;
        _undoButton.ToggleAttribute("disabled", !_document.CanUndo);
        _redoButton.ToggleAttribute("disabled", !_document.CanRedo);
        _undoButton.TextContent = _document.CanUndo ? "Undo " + undo.UndoLabel : "Undo";
        _redoButton.TextContent = _document.CanRedo ? "Redo " + undo.RedoLabel : "Redo";

        string path = _document.Path ?? "Untitled";
        string name = _document.Project.Name.Length > 0 ? _document.Project.Name : "Untitled";
        _projectName.TextContent = _document.IsDirty ? name + " *" : name;
        _projectName.ClassList.Toggle("dirty", _document.IsDirty);
        ((HTMLElement)_statusDirty).Hidden = !_document.IsDirty;
        _statusPath.TextContent = path;
    }

    private void OnValidationCounts(int errors, int warnings)
    {
        _statusErrors.TextContent = errors == 1 ? "1 error" : errors + " errors";
        _statusWarnings.TextContent = warnings == 1 ? "1 warning" : warnings + " warnings";
    }

    private void RefreshLiveStatus()
    {
        Runtime.DirgeSystem? system = _audition.System;
        SetIfChanged(_statusInstances, ref _lastInstances, (system?.InstanceCount ?? 0).ToString());
        SetIfChanged(_statusVoices, ref _lastVoices, (system?.RealVoiceCount ?? 0).ToString());
        SetIfChanged(_statusMs, ref _lastMs, _audition.LastUpdateMs.ToString("0.00") + " ms");
        SetIfChanged(_statusBuses, ref _lastBuses, AudioServer.GetBusCount().ToString());
        string live = _live.StatusText;
        SetIfChanged(_statusLive, ref _lastLive, live);
        ((HTMLElement)_statusLive).Hidden = live.Length == 0;
    }

    private void OnLiveStateChanged()
    {
        _connectButton.TextContent = _live.IsConnected ? "Disconnect" : "Connect…";
        _liveState.Hidden = !_live.IsConnected;
        _liveState.Title = _live.IsConnected ? $"Connected to {_live.Host}:{_live.Port}" : "";
        if (_live.LastError.Length > 0)
        {
            SetMessage(_live.LastError, true);
        }
    }

    /// <summary>Transient status-bar message (10 s), red when <paramref name="error"/>.</summary>
    public void SetMessage(string text, bool error)
    {
        _message = text;
        _messageIsError = error;
        _messageTtl = text.Length == 0 ? 0 : 10;
        _statusMessage.TextContent = text;
        _statusMessage.ClassList.Toggle("error", error);
    }

    private static void SetIfChanged(Element element, ref string last, string text)
    {
        if (last != text)
        {
            last = text;
            element.TextContent = text;
        }
    }

    private static void Listen(Document document, string id, Action action)
        => Required(document, id).AddEventListener("click", new DelegateEventListener(_ => action()));

    private static Element Required(Document document, string id)
        => document.GetElementById(id) ?? throw new InvalidOperationException($"shell document must keep id='{id}'.");
}
#endif
