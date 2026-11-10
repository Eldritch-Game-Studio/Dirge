#nullable enable
using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Runtime;
using Dirge.Tool.Core;
using Godot;

namespace Dirge.Tool.Events;

/// <summary>
/// Event editor: header document (path, bus, macros, parameters, tempo) over a split of the timeline canvas and the
/// inspector document, over the automation lanes document and the curve canvas. Keyboard commands arrive from the
/// shell through <see cref="IToolPaneCommands"/>.
/// </summary>
public sealed class EventEditorPane : IToolPane, IToolPaneCommands
{
    private readonly VSplitContainer _root = new() { Name = "EventEditor", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };

    private ToolContext? _context;
    private EventEditorState? _state;
    private EventEdits? _edits;
    private AssetLengthCache? _lengths;
    private AssetPicker? _assets;
    private TimelineCanvas? _timeline;
    private CurveCanvas? _curve;
    private EventHeaderView? _header;
    private EventInspectorView? _inspector;
    private AutomationView? _automation;
    private Ticker? _ticker;
    private Control? _headerSlot;
    private HSplitContainer? _automationSplit;

    private bool _syncingSelection;
    private bool _renderQueued;
    private double _renderDue;
    private bool _wasPlaying;

    // Clipboard (pane-local; survives event switches).
    private readonly List<ClipItem> _clipboard = new();
    private readonly List<LogicMarker> _clipboardMarkers = new();
    private float _clipboardOrigin;

    public string Title => "Events";
    public Control Root => _root;

    /// <summary>Exposed for tests and the proof scene.</summary>
    public EventEditorState? State => _state;
    public TimelineCanvas? Timeline => _timeline;
    public CurveCanvas? Curve => _curve;

    /// <summary>Pixels (at the current scale) the pane spends outside the timeline: header row, automation panel and the
    /// two split separators. The shell budgets the timeline's share of the center column from it.</summary>
    public float ChromeHeight => _headerSlot is null || _automationSplit is null
        ? 0f
        : _headerSlot.CustomMinimumSize.Y + _automationSplit.CustomMinimumSize.Y + 2f * _root.GetThemeConstant("separation");

    public void Attach(ToolContext context)
    {
        Detach();
        _context = context;
        _state = new EventEditorState(context);
        _edits = new EventEdits(_state);
        _lengths = new AssetLengthCache(_state);
        _assets = new AssetPicker(context.Host);

        _timeline = new TimelineCanvas(_state, _edits, _lengths, context.Audition)
        {
            CopyRequested = () => Copy(),
            PasteRequested = () => Paste(),
            DuplicateRequested = () => Duplicate(),
            DeleteRequested = () => DeleteSelection(),
        };
        _curve = new CurveCanvas(_state, _edits, context.Audition);
        _header = new EventHeaderView(_state, _edits, TogglePlay, () => _timeline.ZoomToFit());
        _inspector = new EventInspectorView(_state, _edits, _assets);
        _automation = new AutomationView(_state, _edits, _curve);

        // Hosts live in plain slots: a container parent would reset their text scale (see ToolDocuments.Slot). All minimum
        // sizes are authored at scale 1 and re-applied by OnScaleChanged. The header costs one toolbar row until a section
        // is shown.
        Control headerSlot = ToolDocuments.Slot(_header.Host, "Header", new Vector2(0, _header.AuthoredHeight));
        headerSlot.SizeFlagsVertical = Control.SizeFlags.Fill;
        _headerSlot = headerSlot;
        EventHeaderView header = _header;
        header.SectionsChanged = () => ToolDocuments.SetAuthoredMinimumSize(headerSlot, new Vector2(0, header.AuthoredHeight));

        var middle = new HSplitContainer { Name = "Middle", SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        middle.AddChild(_timeline);
        Control inspectorSlot = ToolDocuments.Slot(_inspector.Host, "Inspector", new Vector2(320, 0));
        inspectorSlot.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        middle.AddChild(inspectorSlot);

        var bottom = new HSplitContainer { Name = "Automation", SizeFlagsVertical = Control.SizeFlags.Fill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        ToolDocuments.SetAuthoredMinimumSize(bottom, new Vector2(0, 170));
        _automationSplit = bottom;
        Control lanesSlot = ToolDocuments.Slot(_automation.Host, "Lanes", new Vector2(380, 0));
        lanesSlot.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        bottom.AddChild(lanesSlot);
        bottom.AddChild(_curve);

        var lower = new VSplitContainer { Name = "Lower", SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        lower.AddChild(middle);
        lower.AddChild(bottom);

        _root.AddChild(headerSlot);
        _root.AddChild(lower);
        _ticker = new Ticker { OnTick = Tick };
        _root.AddChild(_ticker);
        ToolScale.Changed += OnScaleChanged;

        context.Project.Changed += OnProjectChanged;
        context.Selection.Changed += OnToolSelectionChanged;
        context.Audition.Rebuilt += OnRebuilt;
        _state.SelectionChanged += OnStateSelectionChanged;

        AdoptToolSelection();
        RenderAll();
    }

    public void Detach()
    {
        if (_context is not null)
        {
            _context.Project.Changed -= OnProjectChanged;
            _context.Selection.Changed -= OnToolSelectionChanged;
            _context.Audition.Rebuilt -= OnRebuilt;
        }
        if (_state is not null) _state.SelectionChanged -= OnStateSelectionChanged;
        ToolScale.Changed -= OnScaleChanged;
        _assets?.Dispose();
        foreach (Node child in _root.GetChildren())
        {
            _root.RemoveChild(child);
            child.QueueFree();
        }
        _context = null; _state = null; _edits = null; _lengths = null; _assets = null;
        _timeline = null; _curve = null; _header = null; _inspector = null; _automation = null; _ticker = null;
        _headerSlot = null; _automationSplit = null;
    }

    /// <summary>Authored minimum sizes follow the tool scale; canvases and hosts rescale themselves.</summary>
    private void OnScaleChanged() => ToolDocuments.ApplyMinimumSizes(_root);

    /// <summary>Edit a specific event (the browser does this through <see cref="Selection"/>; tests call it directly).</summary>
    public void Edit(DirgeId eventId)
    {
        if (_state is null) return;
        _state.SetEvent(eventId);
        RenderAll();
    }

    // ---- change propagation ------------------------------------------------------------------------------------------

    private void OnProjectChanged(ChangeScope scope, DirgeId subject)
    {
        if (_state is null) return;
        bool structural = (scope & (ChangeScope.All)) == ChangeScope.All || (scope & ChangeScope.Events) != 0;
        if (structural)
        {
            _lengths?.Clear();
            _state.Refresh();
            if (_state.Event is null) AdoptToolSelection();
            QueueRender(0);
            return;
        }
        if ((scope & ChangeScope.EventContent) != 0 && subject == _state.EventId)
        {
            _state.Bump();
            QueueRender(0);
            return;
        }
        if ((scope & (ChangeScope.Mixer | ChangeScope.Snapshots | ChangeScope.GlobalParameters | ChangeScope.Assets)) != 0)
        {
            if ((scope & ChangeScope.Assets) != 0) _lengths?.Clear();
            // The editor shows only bus/snapshot names: a fader or snapshot-value drag (Changed every frame) must not rebuild it.
            if ((scope & ~(ChangeScope.Mixer | ChangeScope.Snapshots)) == 0 && !NamesChanged()) return;
            _state.Bump();
            QueueRender(0.2);
        }
    }

    private int _namesSignature;

    /// <summary>True (and remembers) when the set of bus/snapshot ids and names differs from the last render.</summary>
    private bool NamesChanged()
    {
        var hash = new HashCode();
        List<Bus> buses = _state!.Project.Mixer.Buses;
        for (int i = 0; i < buses.Count; i++) { hash.Add(buses[i].Id); hash.Add(buses[i].Name); }
        List<Snapshot> snapshots = _state.Project.Snapshots;
        for (int i = 0; i < snapshots.Count; i++) { hash.Add(snapshots[i].Id); hash.Add(snapshots[i].Name); }
        int signature = hash.ToHashCode();
        if (signature == _namesSignature) return false;
        _namesSignature = signature;
        return true;
    }

    private void OnRebuilt() => _timeline?.Invalidate();

    private void OnToolSelectionChanged()
    {
        if (_syncingSelection || _state is null) return;
        int revision = _state.Revision;
        AdoptToolSelection();
        if (_state.Revision != revision) QueueRender(0);
    }

    /// <summary>Follow the tool-wide selection: events open; instruments/tracks/markers open their owner and select in-pane.</summary>
    private void AdoptToolSelection()
    {
        if (_state is null || _context is null) return;
        Selection selection = _context.Selection;
        _syncingSelection = true;
        try
        {
            switch (selection.Kind)
            {
                case SelectionKind.Event:
                    if (selection.Id != _state.EventId) _state.SetEvent(selection.Id);
                    break;
                case SelectionKind.Instrument:
                    if (selection.Owner != _state.EventId) _state.SetEvent(selection.Owner);
                    if (_state.FindInstrument(selection.Id) is { } inst)
                    {
                        _state.SetSheet(inst.Placement.Parameter);
                        _state.SelectInstrument(inst.Id, false);
                    }
                    break;
                case SelectionKind.Track:
                    if (selection.Owner != _state.EventId) _state.SetEvent(selection.Owner);
                    _state.SelectTrack(selection.Id);
                    break;
                case SelectionKind.Marker:
                    if (selection.Owner != _state.EventId) _state.SetEvent(selection.Owner);
                    _state.SetSheet(DirgeId.None);
                    _state.SelectMarker(selection.Id, false);
                    break;
                case SelectionKind.Parameter:
                    if (!selection.Owner.IsNone && selection.Owner != _state.EventId) _state.SetEvent(selection.Owner);
                    if (_state.Event is not null && _state.FindParameter(selection.Id) is not null) _state.SetSheet(selection.Id);
                    break;
            }
        }
        finally { _syncingSelection = false; }
    }

    private void OnStateSelectionChanged()
    {
        if (_state is null || _context is null) return;
        if (!_syncingSelection)
        {
            _syncingSelection = true;
            try
            {
                Selection selection = _context.Selection;
                if (_state.PrimaryInstrument is { } inst) selection.Select(SelectionKind.Instrument, inst.Id, _state.EventId);
                else if (_state.PrimaryMarker is { } marker) selection.Select(SelectionKind.Marker, marker.Id, _state.EventId);
                else if (!_state.SelectedTrack.IsNone) selection.Select(SelectionKind.Track, _state.SelectedTrack, _state.EventId);
                else if (!_state.EventId.IsNone && (selection.Kind != SelectionKind.Event || selection.Id != _state.EventId)) selection.Select(SelectionKind.Event, _state.EventId);
            }
            finally { _syncingSelection = false; }
        }
        QueueRender(0);
    }

    private void QueueRender(double delaySeconds)
    {
        double due = Time.GetTicksMsec() / 1000.0 + delaySeconds;
        if (_renderQueued) { if (due < _renderDue) _renderDue = due; return; }
        _renderQueued = true;
        _renderDue = due;
        _timeline?.Invalidate();
        _curve?.Invalidate();
    }

    private void Tick(double delta)
    {
        if (_state is null) return;
        bool playing = _timeline?.RunningInstance() is not null;
        if (playing != _wasPlaying)
        {
            _wasPlaying = playing;
            if (_header is not null) { _header.Playing = playing; _header.Render(); }
        }
        if (_renderQueued && Time.GetTicksMsec() / 1000.0 >= _renderDue)
        {
            _renderQueued = false;
            RenderAll();
        }
    }

    private void RenderAll()
    {
        if (_header is not null) { _header.Playing = _timeline?.RunningInstance() is not null; _header.Render(); }
        _inspector?.Render();
        _automation?.Render();
        _timeline?.Invalidate();
        _curve?.Invalidate();
    }

    // ---- commands ----------------------------------------------------------------------------------------------------

    public bool Handle(ToolCommand command)
    {
        if (_state is null || _state.Event is null) return false;
        switch (command)
        {
            case ToolCommand.PlayStop: TogglePlay(); return true;
            case ToolCommand.Delete: return DeleteSelection();
            case ToolCommand.Copy: return Copy();
            case ToolCommand.Paste: return Paste();
            case ToolCommand.Duplicate: return Duplicate();
            case ToolCommand.SelectAll: return SelectAll();
            case ToolCommand.Escape: _state.ClearSelection(); return true;
            default: return false;
        }
    }

    public void TogglePlay()
    {
        if (_state is null || _context is null || _state.Event is null) return;
        IAudition audition = _context.Audition;
        bool any = false;
        IReadOnlyList<EventInstance> instances = audition.Instances;
        for (int i = 0; i < instances.Count; i++)
        {
            EventInstance instance = instances[i];
            if (instance.Description.Id != _state.EventId || instance.State == PlaybackState.Stopped) continue;
            instance.Stop(StopMode.AllowFadeout);
            any = true;
        }
        if (!any) audition.Play(_state.EventId);
    }

    private bool DeleteSelection()
    {
        if (_state is null || _edits is null) return false;
        if (_curve is { HasSelectedPoint: true } && _curve.HasFocus()) { _curve.DeleteSelectedPoint(); return true; }
        bool any = false;
        if (_state.SelectedInstruments.Count > 0) { _edits.RemoveInstruments(_state.SelectedInstruments); any = true; }
        if (_state.SelectedMarkers.Count > 0) { _edits.RemoveMarkers(_state.SelectedMarkers); any = true; }
        if (any) _state.Refresh();
        return any;
    }

    private bool SelectAll()
    {
        if (_state?.Event is not { } ev) return false;
        var ids = new List<DirgeId>();
        for (int t = 0; t < ev.Tracks.Count; t++)
            for (int i = 0; i < ev.Tracks[t].Instruments.Count; i++)
                if (ev.Tracks[t].Instruments[i].Placement.Parameter == _state.Sheet) ids.Add(ev.Tracks[t].Instruments[i].Id);
        _state.SelectInstruments(ids, false);
        return true;
    }

    private bool Copy()
    {
        if (_state?.Event is not { } ev) return false;
        if (_state.SelectedInstruments.Count == 0 && _state.SelectedMarkers.Count == 0) return false;
        _clipboard.Clear();
        _clipboardMarkers.Clear();
        float origin = float.MaxValue;
        for (int i = 0; i < _state.SelectedInstruments.Count; i++)
        {
            Instrument? inst = _state.FindInstrument(_state.SelectedInstruments[i], out Track? track);
            if (inst is null || track is null) continue;
            _clipboard.Add(new ClipItem(EventEdits.Clone(inst), ev.Tracks.IndexOf(track)));
            origin = Math.Min(origin, inst.Placement.Start);
        }
        for (int i = 0; i < _state.SelectedMarkers.Count; i++)
        {
            LogicMarker? marker = _state.FindMarker(_state.SelectedMarkers[i]);
            if (marker is null) continue;
            _clipboardMarkers.Add(EventEdits.Clone(marker));
            origin = Math.Min(origin, marker.Position);
        }
        _clipboardOrigin = origin == float.MaxValue ? 0f : origin;
        return true;
    }

    private bool Paste()
    {
        if (_state?.Event is not { } ev || _edits is null || _timeline is null) return false;
        if (_clipboard.Count == 0 && _clipboardMarkers.Count == 0) return false;
        float anchor = (float)_state.Cursor;
        int anchorTrack = _timeline.LastPointerTrack is { } t ? ev.Tracks.IndexOf(t)
            : _state.FindTrack(_state.SelectedTrack) is { } st ? ev.Tracks.IndexOf(st) : -1;
        int minTrack = int.MaxValue;
        for (int i = 0; i < _clipboard.Count; i++) minTrack = Math.Min(minTrack, _clipboard[i].TrackIndex);
        if (anchorTrack < 0) anchorTrack = minTrack == int.MaxValue ? 0 : minTrack;

        var items = new List<(Track, Instrument)>();
        var created = new List<DirgeId>();
        for (int i = 0; i < _clipboard.Count; i++)
        {
            Instrument copy = EventEdits.Clone(_clipboard[i].Instrument);
            copy.Placement.Parameter = _state.Sheet;
            copy.Placement.Start = Math.Max(0, anchor + (copy.Placement.Start - _clipboardOrigin));
            int trackIndex = Math.Clamp(anchorTrack + (_clipboard[i].TrackIndex - minTrack), 0, Math.Max(0, ev.Tracks.Count - 1));
            Track track = ev.Tracks.Count > 0 ? ev.Tracks[trackIndex] : _edits.AddTrack();
            items.Add((track, copy));
            created.Add(copy.Id);
        }
        _edits.AddInstruments(items, items.Count == 1 ? "Paste instrument" : $"Paste {items.Count} instruments");
        if (_state.Sheet.IsNone)
        {
            for (int i = 0; i < _clipboardMarkers.Count; i++)
            {
                LogicMarker copy = EventEdits.Clone(_clipboardMarkers[i]);
                copy.Position = Math.Max(0, anchor + (copy.Position - _clipboardOrigin));
                _edits.AddMarker(copy);
                _state.SelectMarker(copy.Id, true);
            }
        }
        if (created.Count > 0) _state.SelectInstruments(created, false);
        return true;
    }

    private bool Duplicate()
    {
        if (_state?.Event is not { } ev || _edits is null || _lengths is null) return false;
        if (_state.SelectedInstruments.Count == 0 && _state.SelectedMarkers.Count == 0) return false;
        float start = float.MaxValue, end = float.MinValue;
        var sources = new List<(Track, Instrument)>();
        for (int i = 0; i < _state.SelectedInstruments.Count; i++)
        {
            Instrument? inst = _state.FindInstrument(_state.SelectedInstruments[i], out Track? track);
            if (inst is null || track is null) continue;
            sources.Add((track, inst));
            float length = inst.Placement.Length > 0 ? inst.Placement.Length : (float)(_state.Sheet.IsNone ? _lengths.NaturalLength(inst) : 0.1);
            start = Math.Min(start, inst.Placement.Start);
            end = Math.Max(end, inst.Placement.Start + length);
        }
        var markers = new List<LogicMarker>();
        for (int i = 0; i < _state.SelectedMarkers.Count; i++)
        {
            LogicMarker? m = _state.FindMarker(_state.SelectedMarkers[i]);
            if (m is null) continue;
            markers.Add(m);
            start = Math.Min(start, m.Position);
            end = Math.Max(end, m.Position + Math.Max(EventEdits.RegionLength(m), 0.5f));
        }
        float offset = end > start ? end - start : 1f;
        var items = new List<(Track, Instrument)>();
        var created = new List<DirgeId>();
        for (int i = 0; i < sources.Count; i++)
        {
            Instrument copy = EventEdits.Clone(sources[i].Item2);
            copy.Placement.Start += offset;
            items.Add((sources[i].Item1, copy));
            created.Add(copy.Id);
        }
        _edits.AddInstruments(items, items.Count == 1 ? "Duplicate instrument" : $"Duplicate {items.Count} instruments");
        for (int i = 0; i < markers.Count; i++)
        {
            LogicMarker copy = EventEdits.Clone(markers[i]);
            copy.Position += offset;
            _edits.AddMarker(copy);
        }
        if (created.Count > 0) _state.SelectInstruments(created, false);
        return true;
    }

    private readonly record struct ClipItem(Instrument Instrument, int TrackIndex);

}

/// <summary>Per-frame hook for the non-Node pane (deferred document renders, play-state label).</summary>
internal sealed partial class Ticker : Node
{
    public Action<double>? OnTick;
    public override void _Process(double delta) => OnTick?.Invoke(delta);
}
