#nullable enable
#if TOOLS
using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Tool.Core;
using Dirge.Tool.Events;
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
    private HSplitContainer _mainSplit = null!;
    private VSplitContainer _centerSplit = null!;
    private PaneFrame _transportFrame = null!;
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
    private int _lastInstances = -1, _lastVoices = -1, _lastBuses = -1;
    private double _lastMs = double.NaN;
    private string _lastLive = "";

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

        ToolScale.Watch(this);
        BuildLayout();
        ToolScale.Changed += OnScaleChanged;
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

        ToolScale.Changed -= OnScaleChanged;
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
        var layout = new VBoxContainer { Name = "Layout" };
        layout.SetAnchorsPreset(LayoutPreset.FullRect);
        layout.AddThemeConstantOverride("separation", 0);
        AddChild(layout);

        // Minimum sizes are authored at tool scale 1 (ToolDocuments.SetAuthoredMinimumSize) and re-applied on scale changes.
        _menu = ToolDocuments.Create("shell.html");
        Control menuBar = ToolDocuments.Slot(_menu, "MenuBar", new Vector2(0, 28));
        menuBar.SizeFlagsVertical = SizeFlags.Fill;
        layout.AddChild(menuBar);

        // SplitContainer measures its offset from the non-expanding child's minimum size, so the browser (left) and the
        // bottom tabs are Fill-only with a minimum size; the center/top areas take the remaining space. Every pane sits in a
        // PaneFrame whose minimum size is the shell's, not the pane's. The browser/center and top/bottom offsets persist in
        // editor settings (authored pixels, see RestoreSplits).
        _mainSplit = new HSplitContainer { Name = "Main", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        layout.AddChild(_mainSplit);
        _mainSplit.Dragged += offset => ToolSettings.Set(ToolSettings.BrowserSplit, offset / ToolScale.Current);

        _browser = AddPane(PaneRegistry.Browser, "Browser");
        PaneFrame browserFrame = Frame(_browser, new Vector2(260, 0));
        browserFrame.SizeFlagsHorizontal = SizeFlags.Fill;
        _mainSplit.AddChild(browserFrame);

        _centerSplit = new VSplitContainer { Name = "Center", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        _mainSplit.AddChild(_centerSplit);
        _centerSplit.Dragged += offset => ToolSettings.Set(ToolSettings.BottomSplit, offset / ToolScale.Current);
        _centerSplit.Resized += DefaultBottomSplit;

        // Transport docked above the event editor; the transport keeps a fixed (draggable) height, the editor takes the rest.
        var top = new VSplitContainer { Name = "Top", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _centerSplit.AddChild(top);

        _transport = AddPane(PaneRegistry.Transport, "Transport");
        float scale = ToolScale.Current;
        float transportHeight = Mathf.Clamp(_transport.Root.GetCombinedMinimumSize().Y / scale, 136, 200);
        _transportFrame = Frame(_transport, new Vector2(0, transportHeight));
        _transportFrame.SizeFlagsVertical = SizeFlags.Fill;
        top.AddChild(_transportFrame);

        _events = AddPane(PaneRegistry.Events, "Events");
        top.AddChild(Frame(_events, new Vector2(0, 120)));

        _bottomTabs = new TabContainer { Name = "Bottom", SizeFlagsVertical = SizeFlags.Fill, SizeFlagsHorizontal = SizeFlags.ExpandFill, TabAlignment = TabBar.AlignmentMode.Left };
        ToolDocuments.SetAuthoredMinimumSize(_bottomTabs, new Vector2(0, 180));
        _centerSplit.AddChild(_bottomTabs);
        RestoreSplits();

        _mixer = AddPane(PaneRegistry.Mixer, "Mixer");
        _profiler = AddPane(PaneRegistry.Profiler, "Profiler");
        _validation = new ValidationPanel();
        _panes.Add(_validation);
        AddTab(_mixer);
        AddTab(_profiler);
        AddTab(_validation);

        _status = ToolDocuments.Create("shell-status.html");
        Control statusBar = ToolDocuments.Slot(_status, "StatusBar", new Vector2(0, 22));
        statusBar.SizeFlagsVertical = SizeFlags.Fill;
        layout.AddChild(statusBar);
    }

    /// <summary>Documents and canvases rescale themselves; the shell re-applies every authored minimum size and split offset.</summary>
    private void OnScaleChanged()
    {
        ToolDocuments.ApplyMinimumSizes(this);
        RestoreSplits();
    }

    /// <summary>Applies the persisted split offsets (authored pixels) at the current scale; the bottom split falls back to
    /// <see cref="DefaultBottomSplit"/> when the user never dragged it.</summary>
    private void RestoreSplits()
    {
        float scale = ToolScale.Current;
        if (ToolSettings.TryGet(ToolSettings.BrowserSplit, out Variant browser)) _mainSplit.SplitOffsets = new[] { Mathf.RoundToInt(browser.AsSingle() * scale) };
        if (ToolSettings.TryGet(ToolSettings.BottomSplit, out Variant bottom)) _centerSplit.SplitOffsets = new[] { Mathf.RoundToInt(bottom.AsSingle() * scale) };
        else DefaultBottomSplit();
    }

    /// <summary>
    /// Default top/bottom split: the timeline (the event editor minus its header and automation chrome, under the transport)
    /// gets at least 40 % of the center column; whatever remains above the bottom tabs' minimum goes to the tabs. Follows
    /// the center's size (and scale changes) until the user drags the split, which persists an offset instead.
    /// </summary>
    private void DefaultBottomSplit()
    {
        if (_centerSplit is null || _bottomTabs is null || ToolSettings.TryGet(ToolSettings.BottomSplit, out _)) return;
        float height = _centerSplit.Size.Y;
        if (height <= 0f) return;
        float separation = _centerSplit.GetThemeConstant("separation");
        float chrome = _events is EventEditorPane events ? events.ChromeHeight : 0f;
        float fixedTop = _transportFrame.CustomMinimumSize.Y + separation + chrome;
        float maxTop = height - separation - _bottomTabs.CustomMinimumSize.Y;
        float wishedTop = Mathf.Min(fixedTop + 0.4f * height, maxTop);
        // Nothing after the dragger expands, so offset 0 is the end of the container: the dragger sits at size + offset and
        // the top child ends one separation before it. The container clamps to the bottom tabs' minimum.
        _centerSplit.SplitOffsets = new[] { Mathf.RoundToInt(wishedTop + separation - height) };
    }

    private IToolPane AddPane(string typeName, string title)
    {
        IToolPane pane = PaneRegistry.Create(typeName, title);
        pane.Root.Name = title;
        _panes.Add(pane);
        return pane;
    }

    /// <param name="minimum">Frame minimum size authored at tool scale 1.</param>
    private static PaneFrame Frame(IToolPane pane, Vector2 minimum)
    {
        var frame = new PaneFrame
        {
            Name = pane.Title + "Frame",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        ToolDocuments.SetAuthoredMinimumSize(frame, minimum);
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
        int instances = system?.InstanceCount ?? 0;
        int voices = system?.RealVoiceCount ?? 0;
        // 0.1 ms steps: idle-stable, and a change is a same-width digit swap the document host applies without relayout.
        double ms = Math.Round(_audition.LastUpdateMs, 1);
        int buses = AudioServer.GetBusCount();
        if (instances != _lastInstances) { _lastInstances = instances; _statusInstances.TextContent = instances.ToString(); }
        if (voices != _lastVoices) { _lastVoices = voices; _statusVoices.TextContent = voices.ToString(); }
        if (ms != _lastMs) { _lastMs = ms; _statusMs.TextContent = ms.ToString("0.0") + " ms"; }
        if (buses != _lastBuses) { _lastBuses = buses; _statusBuses.TextContent = buses.ToString(); }
        string live = _live.StatusText;
        if (live != _lastLive)
        {
            _lastLive = live;
            _statusLive.TextContent = live;
            ((HTMLElement)_statusLive).Hidden = live.Length == 0;
        }
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

    private static void Listen(Document document, string id, Action action)
        => Required(document, id).AddEventListener("click", new DelegateEventListener(_ => action()));

    private static Element Required(Document document, string id)
        => document.GetElementById(id) ?? throw new InvalidOperationException($"shell document must keep id='{id}'.");
}
#endif
