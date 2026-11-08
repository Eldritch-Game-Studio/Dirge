#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Dirge.Driver;
using Dirge.Profiling;
using Dirge.Project;
using Dirge.Runtime;
using Dirge.Tool.Core;
using Godot;
using NecronomiDom.Dom;
using NecronomiDom.Runtime;

namespace Dirge.Tool.Profiler;

/// <summary>
/// Profiler: live instances, counters, per-bus levels and recordings for either the editor audition or a game reached
/// through <see cref="LiveUpdateClient"/>. Layout: <c>HSplit { profiler.html | VBox { UpdateGraph, RecordingCanvas } }</c>.
/// </summary>
public sealed partial class ProfilerPane : IToolPane
{
    private const string DocumentName = "profiler.html";
    private const double DomRefreshInterval = 1.0 / 15.0;
    private const float MeterFloorDb = -60f;

    private readonly PaneRoot _root;
    private readonly VBoxContainer _canvases;
    private readonly UpdateGraph _graph;
    private readonly RecordingCanvas _timeline;
    private readonly List<(Element Element, string Type, DelegateEventListener Listener)> _listeners = new();
    private readonly List<InstanceRowElements> _rowElements = new();
    private readonly List<BusRowElements> _busElements = new();
    private readonly Dictionary<string, string> _shownText = new(StringComparer.Ordinal);

    private ToolContext? _context;
    private DocumentHost? _host;
    private Control? _hostSlot;
    private ProfilerFeed? _feed;
    private LiveUpdateClient? _client;
    private Recorder? _recorder;
    private bool _armRecorder;
    private Recording? _recording;
    private string _recordingTitle = "No recording";
    private FileDialog? _dialog;
    private double _domClock;
    private int _selectedRow = -1;
    private int _shownInstances = -1, _shownVoices = -1;
    private double _shownUpdateMs = double.NaN, _shownTime = double.NaN;
    private int _recordingTicksShown = -1;

    private Element? _instanceRows;
    private Element? _instancesEmpty;
    private Element? _busRows;
    private Element? _sourceStatus;
    private Element? _recStatus;
    private Element? _recCompare;
    private HTMLInputElement? _hostInput;
    private HTMLInputElement? _pathInput;

    public ProfilerPane()
    {
        _root = new PaneRoot { Pane = this, Name = "Profiler", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _canvases = new VBoxContainer { Name = "Canvases", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        ToolDocuments.SetAuthoredMinimumSize(_canvases, new Vector2(300, 0));
        _graph = new UpdateGraph { Name = "UpdateGraph" };
        ToolDocuments.SetAuthoredMinimumSize(_graph, new Vector2(300, 140));
        _timeline = new RecordingCanvas { Name = "RecordingCanvas" };
        _canvases.AddChild(_graph);
        _canvases.AddChild(_timeline);
        _root.AddChild(_canvases);
        _timeline.BarClicked += OnBarClicked;
    }

    public string Title => "Profiler";
    public Control Root => _root;

    /// <summary>The recording shown in the timeline (last stopped or loaded).</summary>
    public Recording? Recording => _recording;
    public bool IsRecording => _recorder is not null;
    public ProfilerFeed? Feed => _feed;
    public UpdateGraph Graph => _graph;
    public RecordingCanvas Timeline => _timeline;
    /// <summary>Outcome text of the last <see cref="Replay"/> (also shown in the document).</summary>
    public string CompareResult { get; private set; } = "";
    public Divergence? LastDivergence { get; private set; }

    // ---- lifecycle -----------------------------------------------------------------------------------------------

    public void Attach(ToolContext context)
    {
        _context = context;
        _host = ToolDocuments.Create(DocumentName);
        _host.Name = "ProfilerDocument";
        _hostSlot = ToolDocuments.Slot(_host, "Document", new Vector2(520, 0));
        _root.AddChild(_hostSlot);
        _root.MoveChild(_hostSlot, 0);
        ToolScale.Changed += OnScaleChanged;

        if (_host.IsNodeReady()) BindDocument();
        else _host.Ready += BindDocument;

        UseEditor();
        _context.Audition.Rebuilt += OnAuditionRebuilt;
        _context.Selection.Changed += OnSelectionChanged;
        // Hidden bottom tab: no polling, graph samples or DOM writes until the tab is shown again.
        _root.VisibilityChanged += OnRootVisibilityChanged;
        OnRootVisibilityChanged();
    }

    public void Detach()
    {
        StopRecording();
        if (_context is not null)
        {
            _context.Audition.Rebuilt -= OnAuditionRebuilt;
            _context.Selection.Changed -= OnSelectionChanged;
        }
        _feed?.Dispose();
        _feed = null;
        if (_client is not null)
        {
            _client.QueueFree();
            _client = null;
        }
        if (_dialog is not null)
        {
            _dialog.QueueFree();
            _dialog = null;
        }
        UnbindDocument();
        ToolScale.Changed -= OnScaleChanged;
        if (_hostSlot is not null)
        {
            _root.RemoveChild(_hostSlot);
            _hostSlot.QueueFree();
            _hostSlot = null;
            _host = null;
        }
        _root.VisibilityChanged -= OnRootVisibilityChanged;
        _root.SetProcess(false);
        _graph.Clear();
        _timeline.SetRecording(null, "No recording");
        _rowElements.Clear();
        _busElements.Clear();
        _shownText.Clear();
        _context = null;
    }

    private void OnRootVisibilityChanged() => _root.SetProcess(_context is not null && _root.IsVisibleInTree());

    private void OnScaleChanged() => ToolDocuments.ApplyMinimumSizes(_root);

    // ---- document binding ----------------------------------------------------------------------------------------

    private void BindDocument()
    {
        if (_host is null) return;
        if (_host.Document is not { } document)
        {
            _host.LoadDocument(ToolDocuments.UiRoot + DocumentName);
            document = _host.Document;
        }

        _instanceRows = Require(document, "instance-rows");
        _instancesEmpty = Require(document, "instances-empty");
        _busRows = Require(document, "bus-rows");
        _sourceStatus = Require(document, "source-status");
        _recStatus = Require(document, "rec-status");
        _recCompare = Require(document, "rec-compare");
        _hostInput = (HTMLInputElement)Require(document, "host");
        _pathInput = (HTMLInputElement)Require(document, "rec-path");

        Listen(document, "src-editor", "click", UseEditor);
        Listen(document, "src-game", "click", ConnectFromInput);
        Listen(document, "connect", "click", ConnectFromInput);
        Listen(document, "rec-start", "click", () => StartRecording());
        Listen(document, "rec-stop", "click", () => StopRecording());
        Listen(document, "rec-save", "click", () => OpenDialog(save: true));
        Listen(document, "rec-load", "click", () => OpenDialog(save: false));
        Listen(document, "rec-replay", "click", () => Replay());

        _rowElements.Clear();
        _busElements.Clear();
        _shownText.Clear();
        RefreshSourceUi();
        RefreshRecordingUi();
    }

    private void UnbindDocument()
    {
        foreach ((Element element, string type, DelegateEventListener listener) in _listeners) element.RemoveEventListener(type, listener);
        _listeners.Clear();
        _instanceRows = null; _instancesEmpty = null; _busRows = null; _sourceStatus = null; _recStatus = null; _recCompare = null;
        _hostInput = null; _pathInput = null;
    }

    private static Element Require(Document document, string id)
        => document.GetElementById(id) ?? throw new InvalidOperationException($"profiler.html must keep id='{id}'.");

    private void Listen(Document document, string id, string type, Action action)
    {
        Element element = Require(document, id);
        var listener = new DelegateEventListener(_ => action());
        element.AddEventListener(type, listener);
        _listeners.Add((element, type, listener));
    }

    private void Listen(Element element, string type, Action action)
    {
        var listener = new DelegateEventListener(_ => action());
        element.AddEventListener(type, listener);
        _listeners.Add((element, type, listener));
    }

    private void SetText(Element? element, string key, string text)
    {
        if (element is null) return;
        if (_shownText.TryGetValue(key, out string? shown) && shown == text) return;
        _shownText[key] = text;
        element.TextContent = text;
    }

    private static void SetEnabled(Element? element, bool enabled)
    {
        if (element is not null) element.ToggleAttribute("disabled", !enabled);
    }

    private static void SetClass(Element? element, string className, bool on)
    {
        if (element is null) return;
        element.ClassList.Toggle(className, on);
    }

    private static void SetHidden(Element? element, bool hidden)
    {
        if (element is HTMLElement html) html.Hidden = hidden;
    }

    // ---- sources -------------------------------------------------------------------------------------------------

    /// <summary>Switch to the editor audition.</summary>
    public void UseEditor()
    {
        if (_context is null) return;
        if (_feed is EditorFeed) { RefreshSourceUi(); return; }
        _feed?.Dispose();
        if (_client is not null)
        {
            _client.QueueFree();
            _client = null;
        }
        _feed = new EditorFeed(_context.Audition, _context.Project);
        _graph.Clear();
        if (_context.Audition.System is not null) _graph.VoiceCapacity = _context.Audition.System.MaxRealVoices;
        RefreshSourceUi();
        RefreshRecordingUi();
    }

    private void ConnectFromInput()
    {
        string text = _hostInput?.Value ?? "";
        string host = "127.0.0.1";
        int port = LiveProtocol.DefaultPort;
        int colon = text.LastIndexOf(':');
        if (colon >= 0)
        {
            if (colon > 0) host = text[..colon].Trim();
            if (!int.TryParse(text[(colon + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port)) port = LiveProtocol.DefaultPort;
        }
        else if (text.Trim().Length > 0)
        {
            host = text.Trim();
        }
        ConnectToGame(host, port);
    }

    /// <summary>
    /// Switch to a running game's live-update host. Inside the editor shell the pane reads the shell's shared
    /// <see cref="LiveUpdateClient"/> (connecting it when idle) because the game keeps one editor connection; without a
    /// shared client (overlay) it owns one.
    /// </summary>
    public void ConnectToGame(string host, int port)
    {
        if (_context is null) return;
        StopRecording();
        _feed?.Dispose();
        if (_client is not null)
        {
            // Leave the tree now (not at QueueFree time) so SharedClient() cannot pick the pane's own client.
            _context.Host.RemoveChild(_client);
            _client.QueueFree();
            _client = null;
        }
        LiveUpdateClient? shared = SharedClient();
        if (shared is not null)
        {
            if (!shared.IsConnected || shared.Host != host || shared.Port != port) shared.Connect(host, port);
            _feed = new GameFeed(shared, host, port, connect: false);
        }
        else
        {
            _client = new LiveUpdateClient { Name = "ProfilerLiveClient" };
            _context.Host.AddChild(_client);
            _feed = new GameFeed(_client, host, port);
        }
        _graph.Clear();
        RefreshSourceUi();
        RefreshRecordingUi();
    }

    /// <summary>The shell's live client: the <see cref="LiveUpdateClient"/> child of the host (the pane's own client is never in the tree here).</summary>
    private LiveUpdateClient? SharedClient()
    {
        foreach (Godot.Node child in _context!.Host.GetChildren())
        {
            if (child is LiveUpdateClient client) return client;
        }
        return null;
    }

    private void RefreshSourceUi()
    {
        if (_host?.Document is not { } document) return;
        bool game = _feed is GameFeed;
        SetClass(document.GetElementById("src-editor"), "selected", !game);
        SetClass(document.GetElementById("src-game"), "selected", game);
        SetText(_sourceStatus, "source", _feed?.Status ?? "No source");
    }

    private void OnAuditionRebuilt()
    {
        if (_context?.Audition.System is { } system) _graph.VoiceCapacity = system.MaxRealVoices;
        if (_armRecorder)
        {
            // Restart() raised this before the fresh system's first Update: attach now so tick 0 is captured.
            _armRecorder = false;
            if (_context?.Audition.System is { Recorder: null } fresh) AttachRecorder(fresh);
            return;
        }
        // The recorder is bound to the old system; keep what it captured.
        if (_recorder is not null) StopRecording();
    }

    // ---- per-frame ----------------------------------------------------------------------------------------------

    internal void Tick(double delta)
    {
        if (_feed is null) return;
        double wall = Godot.Time.GetTicksUsec() * 1e-6;
        if (_feed.Poll())
        {
            ProfilerStats stats = _feed.Stats;
            _graph.Push(wall, (float)stats.UpdateMs, stats.RealVoices);
        }
        _graph.Advance(wall);

        _domClock += delta;
        if (_domClock < DomRefreshInterval) return;
        _domClock = 0;
        RefreshDom();
    }

    private void RefreshDom()
    {
        if (_host?.Document is null || _feed is null) return;
        _feed.PollRows();
        ProfilerStats stats = _feed.Stats;
        Document document = _host.Document;

        if (stats.Instances != _shownInstances) { _shownInstances = stats.Instances; SetText(document.GetElementById("c-instances"), "c-instances", stats.Instances.ToString(CultureInfo.InvariantCulture)); }
        if (stats.RealVoices != _shownVoices) { _shownVoices = stats.RealVoices; SetText(document.GetElementById("c-voices"), "c-voices", stats.RealVoices.ToString(CultureInfo.InvariantCulture)); }
        // 0.1 ms / 0.1 s steps keep the counters idle-stable; same-width digit changes are retained text swaps, not relayouts.
        double updateMs = Math.Round(stats.UpdateMs, 1);
        if (updateMs != _shownUpdateMs) { _shownUpdateMs = updateMs; SetText(document.GetElementById("c-update"), "c-update", updateMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms"); }
        double time = Math.Round(stats.Time, 1);
        if (time != _shownTime) { _shownTime = time; SetText(document.GetElementById("c-time"), "c-time", time.ToString("0.0", CultureInfo.InvariantCulture) + " s"); }
        SetText(document.GetElementById("c-lufs-m"), "c-lufs-m", FormatLufs(stats.MomentaryLufs));
        SetText(document.GetElementById("c-lufs-s"), "c-lufs-s", FormatLufs(stats.ShortTermLufs));
        SetText(document.GetElementById("c-lufs-i"), "c-lufs-i", FormatLufs(stats.IntegratedLufs));
        SetText(document.GetElementById("c-peak"), "c-peak", float.IsNaN(stats.TruePeakDbtp) ? "—" : float.IsInfinity(stats.TruePeakDbtp) ? "silent" : stats.TruePeakDbtp.ToString("0.0", CultureInfo.InvariantCulture) + " dBTP");
        SetText(_sourceStatus, "source", _feed.Status);

        RefreshInstances(document);
        RefreshBuses(document);

        if (_recorder is not null)
        {
            int ticks = _recorder.Recording.Ticks.Count;
            if (ticks != _recordingTicksShown)
            {
                _recordingTicksShown = ticks;
                SetText(_recStatus, "rec-status", "Recording… " + ticks.ToString(CultureInfo.InvariantCulture) + " ticks");
            }
        }
    }

    private static string FormatLufs(float value)
    {
        if (float.IsNaN(value)) return "—";
        if (float.IsNegativeInfinity(value)) return "silent";
        return value.ToString("0.0", CultureInfo.InvariantCulture) + " LUFS";
    }

    private sealed class InstanceRowElements
    {
        public Element Row = null!;
        public Element Handle = null!;
        public Element Event = null!;
        public Element State = null!;
        public Element Position = null!;
        public Element Virtual = null!;
        public Element Parameters = null!;
        public Element Stop = null!;
        public int LiveHandle;
        public string EventPath = "";
    }

    private void RefreshInstances(Document document)
    {
        IReadOnlyList<ProfilerInstanceRow> rows = _feed!.Instances;
        if (_instanceRows is null) return;
        for (int i = 0; i < rows.Count; i++)
        {
            InstanceRowElements row = i < _rowElements.Count ? _rowElements[i] : CreateInstanceRow(document, i);
            ProfilerInstanceRow data = rows[i];
            row.LiveHandle = data.Handle;
            row.EventPath = data.Event;
            SetHidden(row.Row, false);
            string prefix = "row" + i.ToString(CultureInfo.InvariantCulture) + ".";
            SetText(row.Handle, prefix + "h", data.Handle.ToString(CultureInfo.InvariantCulture));
            SetText(row.Event, prefix + "e", data.Event);
            SetText(row.State, prefix + "s", data.State);
            SetText(row.Position, prefix + "p", data.Position.ToString("0.00", CultureInfo.InvariantCulture) + " s");
            SetText(row.Virtual, prefix + "v", data.Virtual ? "virt" : "—");
            SetText(row.Parameters, prefix + "a", data.Parameters.Length == 0 ? "—" : data.Parameters);
            SetClass(row.Virtual, "on", data.Virtual);
            SetClass(row.State, "playing", data.State == nameof(PlaybackState.Playing) || data.State == nameof(PlaybackState.Sustaining));
            SetClass(row.State, "stopping", data.State == nameof(PlaybackState.Stopping));
            SetClass(row.Row, "selected", i == _selectedRow);
        }
        for (int i = rows.Count; i < _rowElements.Count; i++)
        {
            SetHidden(_rowElements[i].Row, true);
            _rowElements[i].LiveHandle = 0;
        }
        SetHidden(_instancesEmpty, rows.Count > 0);
    }

    private InstanceRowElements CreateInstanceRow(Document document, int index)
    {
        var row = new InstanceRowElements();
        row.Row = document.CreateElement("div");
        row.Row.ClassName = "row";
        row.Handle = Cell(document, row.Row, "c-handle");
        row.Event = Cell(document, row.Row, "c-event");
        row.State = Cell(document, row.Row, "c-state");
        row.Position = Cell(document, row.Row, "c-pos");
        row.Virtual = Cell(document, row.Row, "c-virt");
        row.Parameters = Cell(document, row.Row, "c-params");
        var actions = document.CreateElement("p");
        actions.ClassName = "c-act";
        row.Stop = document.CreateElement("button");
        row.Stop.ClassName = "small";
        row.Stop.SetAttribute("type", "button");
        row.Stop.TextContent = "Stop";
        actions.AppendChild(row.Stop);
        row.Row.AppendChild(actions);
        _instanceRows!.AppendChild(row.Row);
        _rowElements.Add(row);

        Listen(row.Stop, "click", () =>
        {
            if (row.LiveHandle != 0) _feed?.StopInstance(row.LiveHandle, immediate: false);
        });
        Listen(row.Event, "click", () =>
        {
            _selectedRow = index;
            SelectEventByPath(row.EventPath);
        });
        return row;
    }

    private static Element Cell(Document document, Element row, string className)
    {
        Element cell = document.CreateElement("p");
        cell.ClassName = className;
        row.AppendChild(cell);
        return cell;
    }

    private sealed class BusRowElements
    {
        public Element Root = null!;
        public Element Name = null!;
        public Element Db = null!;
        public HTMLElement Low = null!, Mid = null!, High = null!;
        public float ShownDb = float.NaN;
        public int ShownLow = -1, ShownMid = -1, ShownHigh = -1;
    }

    // Segment boundaries of the horizontal bus meter as fractions of the −60..0 dB range (profiler.css: .fill.low/.mid/.high).
    private const float MeterMidFrom = (-12f - MeterFloorDb) / -MeterFloorDb;
    private const float MeterHighFrom = (-1f - MeterFloorDb) / -MeterFloorDb;

    private void RefreshBuses(Document document)
    {
        if (_busRows is null) return;
        IReadOnlyList<ProfilerBusLevel> buses = _feed!.Buses;
        for (int i = 0; i < buses.Count; i++)
        {
            BusRowElements row = i < _busElements.Count ? _busElements[i] : CreateBusRow(document);
            ProfilerBusLevel level = buses[i];
            SetHidden(row.Root, false);
            SetText(row.Name, "bus" + i.ToString(CultureInfo.InvariantCulture) + ".n", level.Bus);
            // 0.5 dB readout steps, fixed width: a change is a retained digit swap, not a relayout.
            float peak = MathF.Round(MathF.Max(level.Left, level.Right) * 2f) / 2f;
            if (peak != row.ShownDb)
            {
                row.ShownDb = peak;
                row.Db.TextContent = peak <= MeterFloorDb ? "-inf dB" : peak.ToString("+00.0;-00.0;+00.0", CultureInfo.InvariantCulture) + " dB";
            }
            // Three fixed segments scaled from the left through inline transforms (compositor-only updates, no class toggles).
            float f = Mathf.Clamp((MathF.Max(level.Left, level.Right) - MeterFloorDb) / -MeterFloorDb, 0f, 1f);
            int low = (int)MathF.Round(Math.Min(f, MeterMidFrom) / MeterMidFrom * 1000f);
            int mid = (int)MathF.Round(Mathf.Clamp((f - MeterMidFrom) / (MeterHighFrom - MeterMidFrom), 0f, 1f) * 1000f);
            int high = (int)MathF.Round(Mathf.Clamp((f - MeterHighFrom) / (1f - MeterHighFrom), 0f, 1f) * 1000f);
            if (low != row.ShownLow) { row.ShownLow = low; row.Low.Style.SetProperty("transform", Browser.Dom.ScaleX(low / 1000f)); }
            if (mid != row.ShownMid) { row.ShownMid = mid; row.Mid.Style.SetProperty("transform", Browser.Dom.ScaleX(mid / 1000f)); }
            if (high != row.ShownHigh) { row.ShownHigh = high; row.High.Style.SetProperty("transform", Browser.Dom.ScaleX(high / 1000f)); }
        }
        for (int i = buses.Count; i < _busElements.Count; i++) SetHidden(_busElements[i].Root, true);
    }

    private BusRowElements CreateBusRow(Document document)
    {
        var row = new BusRowElements();
        row.Root = document.CreateElement("div");
        row.Root.ClassName = "bus";
        Element head = document.CreateElement("div");
        head.ClassName = "bus-head";
        row.Name = document.CreateElement("p");
        row.Name.ClassName = "name";
        row.Db = document.CreateElement("p");
        row.Db.ClassName = "db";
        head.AppendChild(row.Name);
        head.AppendChild(row.Db);
        Element meter = document.CreateElement("div");
        meter.ClassName = "meter horizontal";
        row.Low = Segment(document, meter, "fill low");
        row.Mid = Segment(document, meter, "fill mid");
        row.High = Segment(document, meter, "fill high");
        row.Root.AppendChild(head);
        row.Root.AppendChild(meter);
        _busRows!.AppendChild(row.Root);
        _busElements.Add(row);
        return row;
    }

    private static HTMLElement Segment(Document document, Element meter, string className)
    {
        var segment = (HTMLElement)document.CreateElement("div");
        segment.ClassName = className;
        segment.Style.SetProperty("transform", Browser.Dom.ScaleX(0f)); // present from the first layout: later writes are compositor-only
        meter.AppendChild(segment);
        return segment;
    }

    // ---- selection -----------------------------------------------------------------------------------------------

    private void SelectEventByPath(string path)
    {
        if (_context is null) return;
        List<AudioEvent> events = _context.Project.Project.Events;
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i].Path == path)
            {
                _context.Selection.Select(SelectionKind.Event, events[i].Id);
                return;
            }
        }
    }

    private void OnBarClicked(string eventPath, int handle) => SelectEventByPath(eventPath);

    private void OnSelectionChanged()
    {
        if (_context is null || _context.Selection.Kind == SelectionKind.Event) return;
        _selectedRow = -1;
    }

    // ---- recording -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Start recording the editor audition. A replay drives a fresh <see cref="DirgeSystem"/>, so unless the current system has
    /// never ticked the audition is restarted (<see cref="IAudition.Restart"/>) and the recorder attaches before the new
    /// system's first <c>Update</c>. False when the source cannot be recorded.
    /// </summary>
    public bool StartRecording()
    {
        if (_recorder is not null) return true;
        if (_context is null || _feed is not EditorFeed)
        {
            SetText(_recStatus, "rec-status", "Recording needs the editor source");
            return false;
        }
        DirgeSystem? system = _context.Audition.System;
        if (system is { Time: 0, Recorder: null })
        {
            AttachRecorder(system);
            return true;
        }
        _armRecorder = true;
        _context.Audition.Restart();
        if (_armRecorder)
        {
            // Rebuilt did not fire synchronously; the handler attaches when it does.
            SetText(_recStatus, "rec-status", "Restarting audition…");
        }
        return true;
    }

    private void AttachRecorder(DirgeSystem system)
    {
        _recorder = new Recorder(system);
        _recordingTicksShown = -1;
        ClearCompare();
        RefreshRecordingUi();
    }

    /// <summary>Stop the active recorder; the result becomes the shown recording.</summary>
    public Recording? StopRecording()
    {
        if (_recorder is null) return null;
        _recording = _recorder.Stop();
        _recorder = null;
        _recordingTitle = "Recording · " + _recording.Ticks.Count.ToString(CultureInfo.InvariantCulture) + " ticks · " + Duration(_recording).ToString("0.00", CultureInfo.InvariantCulture) + " s";
        _timeline.SetRecording(_recording, _recordingTitle);
        RefreshRecordingUi();
        return _recording;
    }

    public void SaveRecording(string path)
    {
        if (_recording is null) return;
        string json = RecordingJson.Serialize(_recording);
        using FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (file is null)
        {
            SetText(_recStatus, "rec-status", "Save failed: " + FileAccess.GetOpenError());
            return;
        }
        file.StoreString(json);
        if (_pathInput is not null) _pathInput.Value = Localize(path);
        _recordingTitle = Localize(path) + " · " + _recording.Ticks.Count.ToString(CultureInfo.InvariantCulture) + " ticks";
        _timeline.SetRecording(_recording, _recordingTitle);
        RefreshRecordingUi();
        SetText(_recStatus, "rec-status", "Saved " + Localize(path));
    }

    public bool LoadRecording(string path)
    {
        if (!FileAccess.FileExists(path))
        {
            SetText(_recStatus, "rec-status", "Not found: " + Localize(path));
            return false;
        }
        string json = FileAccess.GetFileAsString(path);
        Recording recording;
        try
        {
            recording = RecordingJson.Deserialize(json);
        }
        catch (Exception error)
        {
            SetText(_recStatus, "rec-status", "Load failed: " + error.Message);
            return false;
        }
        StopRecording();
        _recording = recording;
        ClearCompare();
        if (_pathInput is not null) _pathInput.Value = Localize(path);
        _recordingTitle = Localize(path) + " · " + recording.Ticks.Count.ToString(CultureInfo.InvariantCulture) + " ticks · " + Duration(recording).ToString("0.00", CultureInfo.InvariantCulture) + " s";
        _timeline.SetRecording(_recording, _recordingTitle);
        RefreshRecordingUi();
        return true;
    }

    /// <summary>Replays the shown recording against the edited project and compares command streams.</summary>
    public Divergence? Replay()
    {
        if (_recording is null || _context is null) return null;
        Recording replayed = Replayer.Replay(_recording, _context.Project.Project, null, new AssetCatalog());
        Divergence? divergence = Replayer.Compare(_recording, replayed);
        LastDivergence = divergence;
        if (divergence is null)
        {
            int commands = 0;
            for (int i = 0; i < _recording.Ticks.Count; i++) commands += _recording.Ticks[i].Commands.Count;
            CompareResult = "Compare: identical · " + _recording.Ticks.Count.ToString(CultureInfo.InvariantCulture) + " ticks, " + commands.ToString(CultureInfo.InvariantCulture) + " commands";
            _timeline.SetDivergence(double.NaN);
        }
        else
        {
            Divergence d = divergence.Value;
            CompareResult = "Compare: diverged at tick " + d.Tick.ToString(CultureInfo.InvariantCulture) + " #" + d.Index.ToString(CultureInfo.InvariantCulture) + " · expected " + d.Expected + " · actual " + d.Actual;
            double time = d.Tick >= 0 && d.Tick < _recording.Ticks.Count ? _recording.Ticks[d.Tick].Time : _recording.Ticks.Count > 0 ? Duration(_recording) : 0;
            _timeline.SetDivergence(time);
        }
        SetText(_recCompare, "rec-compare", CompareResult);
        SetClass(_recCompare, "diverged", divergence is not null);
        SetHidden(_recCompare, false);
        return divergence;
    }

    private void ClearCompare()
    {
        CompareResult = "";
        LastDivergence = null;
        SetHidden(_recCompare, true);
        _timeline.SetDivergence(double.NaN);
    }

    private static double Duration(Recording recording)
    {
        if (recording.Ticks.Count == 0) return 0;
        RecordedTick last = recording.Ticks[^1];
        return last.Time + last.Dt;
    }

    private void RefreshRecordingUi()
    {
        if (_host?.Document is not { } document) return;
        bool recording = _recorder is not null;
        bool canRecord = !recording && _feed?.RecordableSystem is not null;
        SetEnabled(document.GetElementById("rec-start"), canRecord);
        SetClass(document.GetElementById("rec-start"), "active", recording);
        SetEnabled(document.GetElementById("rec-stop"), recording);
        SetEnabled(document.GetElementById("rec-save"), _recording is not null && !recording);
        SetEnabled(document.GetElementById("rec-load"), !recording);
        SetEnabled(document.GetElementById("rec-replay"), _recording is not null && !recording);
        if (recording) SetText(_recStatus, "rec-status", "Recording…");
        else if (_recording is not null) SetText(_recStatus, "rec-status", _recordingTitle);
        else SetText(_recStatus, "rec-status", _feed is GameFeed ? "No recording · recording needs the editor source" : "No recording");
    }

    private void OpenDialog(bool save)
    {
        if (_context is null) return;
        if (_dialog is null)
        {
            _dialog = new FileDialog
            {
                Name = "ProfilerRecordingDialog",
                Access = FileDialog.AccessEnum.Filesystem,
                UseNativeDialog = false,
                Filters = new[] { "*.json ; Dirge recording" },
                Size = new Vector2I(820, 560),
            };
            _context.Host.AddChild(_dialog);
        }
        _dialog.FileMode = save ? FileDialog.FileModeEnum.SaveFile : FileDialog.FileModeEnum.OpenFile;
        _dialog.Title = save ? "Save recording" : "Load recording";
        string current = _pathInput?.Value is { Length: > 0 } typed ? typed : "user://rec.json";
        _dialog.CurrentPath = ProjectSettings.GlobalizePath(current);
        foreach (Callable callable in DialogCallables(_dialog)) _dialog.Disconnect(FileDialog.SignalName.FileSelected, callable);
        _dialog.Connect(FileDialog.SignalName.FileSelected, Callable.From<string>(path =>
        {
            if (save) SaveRecording(path); else LoadRecording(path);
        }), (uint)GodotObject.ConnectFlags.OneShot);
        _dialog.PopupCentered();
    }

    private static IEnumerable<Callable> DialogCallables(FileDialog dialog)
    {
        foreach (Godot.Collections.Dictionary connection in dialog.GetSignalConnectionList(FileDialog.SignalName.FileSelected))
        {
            yield return connection["callable"].AsCallable();
        }
    }

    /// <summary>Turns an absolute path back into <c>user://</c>/<c>res://</c> when it lives there.</summary>
    private static string Localize(string path)
    {
        if (path.StartsWith("user://", StringComparison.Ordinal) || path.StartsWith("res://", StringComparison.Ordinal)) return path;
        string user = ProjectSettings.GlobalizePath("user://");
        if (path.StartsWith(user, StringComparison.Ordinal)) return "user://" + path[user.Length..];
        string res = ProjectSettings.GlobalizePath("res://");
        if (path.StartsWith(res, StringComparison.Ordinal)) return "res://" + path[res.Length..];
        return path;
    }

    /// <summary>The pane's Control; forwards <c>_Process</c> to the pane.</summary>
    private sealed partial class PaneRoot : HSplitContainer
    {
        public ProfilerPane? Pane { get; init; }
        public override void _Process(double delta) => Pane?.Tick(delta);
    }
}
