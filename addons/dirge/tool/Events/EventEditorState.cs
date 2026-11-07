#nullable enable
using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Tool.Core;

namespace Dirge.Tool.Events;

/// <summary>
/// State shared by the event editor's sub-views: the edited event, the in-pane selection, the active sheet, view
/// settings. Mutations of the project go through <see cref="EventEdits"/>; this type only tracks what the views show.
/// </summary>
public sealed class EventEditorState
{
    private readonly ToolContext _context;
    private readonly List<DirgeId> _selectedInstruments = new();
    private readonly List<DirgeId> _selectedMarkers = new();
    private int _revision;

    public EventEditorState(ToolContext context) => _context = context;

    public ToolContext Context => _context;
    public ProjectDocument Document => _context.Project;
    public AudioProject Project => _context.Project.Project;

    public DirgeId EventId { get; private set; }
    public AudioEvent? Event { get; private set; }

    /// <summary>Incremented on every change to the edited event's content or to the view selection/sheet.</summary>
    public int Revision => _revision;

    /// <summary><see cref="DirgeId.None"/> = timeline; otherwise a local or global parameter id.</summary>
    public DirgeId Sheet { get; private set; } = DirgeId.None;
    public IReadOnlyList<DirgeId> SelectedInstruments => _selectedInstruments;
    public IReadOnlyList<DirgeId> SelectedMarkers => _selectedMarkers;
    public DirgeId SelectedTrack { get; private set; } = DirgeId.None;
    /// <summary>Automation lane shown in the curve editor.</summary>
    public DirgeId SelectedLane { get; private set; } = DirgeId.None;

    public bool SnapToGrid { get; set; } = true;
    /// <summary>Edit cursor on the timeline (seconds) or on the active sheet (parameter units).</summary>
    public double Cursor { get; set; }

    /// <summary>Raised after any selection/sheet change (not for project edits; subscribe to <see cref="ProjectDocument.Changed"/>).</summary>
    public event Action? SelectionChanged;

    public void SetEvent(DirgeId id)
    {
        AudioEvent? ev = FindEvent(id);
        EventId = ev is null ? DirgeId.None : id;
        Event = ev;
        _selectedInstruments.Clear();
        _selectedMarkers.Clear();
        SelectedTrack = DirgeId.None;
        SelectedLane = DirgeId.None;
        Sheet = DirgeId.None;
        Cursor = 0;
        Bump();
        SelectionChanged?.Invoke();
    }

    /// <summary>Re-resolve the event after a project reload (object identity may have changed) and drop stale selection ids.</summary>
    public void Refresh()
    {
        Event = FindEvent(EventId);
        if (Event is null)
        {
            EventId = DirgeId.None;
            _selectedInstruments.Clear();
            _selectedMarkers.Clear();
            SelectedTrack = DirgeId.None;
            SelectedLane = DirgeId.None;
            Sheet = DirgeId.None;
        }
        else
        {
            for (int i = _selectedInstruments.Count - 1; i >= 0; i--)
                if (FindInstrument(_selectedInstruments[i]) is null) _selectedInstruments.RemoveAt(i);
            for (int i = _selectedMarkers.Count - 1; i >= 0; i--)
                if (FindMarker(_selectedMarkers[i]) is null) _selectedMarkers.RemoveAt(i);
            if (!SelectedTrack.IsNone && FindTrack(SelectedTrack) is null) SelectedTrack = DirgeId.None;
            if (!SelectedLane.IsNone && FindLane(SelectedLane) is null) SelectedLane = DirgeId.None;
            if (!Sheet.IsNone && FindParameter(Sheet) is null) Sheet = DirgeId.None;
        }
        Bump();
    }

    public void Bump() => _revision++;

    public void SetSheet(DirgeId parameter)
    {
        if (Sheet == parameter) return;
        Sheet = parameter;
        _selectedInstruments.Clear();
        _selectedMarkers.Clear();
        Cursor = parameter.IsNone ? 0 : FindParameter(parameter)?.Default ?? 0;
        Bump();
        SelectionChanged?.Invoke();
    }

    public void SelectInstrument(DirgeId id, bool additive)
    {
        if (!additive)
        {
            _selectedInstruments.Clear();
            _selectedMarkers.Clear();
            SelectedTrack = DirgeId.None;
        }
        if (additive && _selectedInstruments.Contains(id)) _selectedInstruments.Remove(id);
        else if (!_selectedInstruments.Contains(id)) _selectedInstruments.Add(id);
        Bump();
        SelectionChanged?.Invoke();
    }

    public void SelectInstruments(List<DirgeId> ids, bool additive)
    {
        if (!additive)
        {
            _selectedInstruments.Clear();
            _selectedMarkers.Clear();
            SelectedTrack = DirgeId.None;
        }
        for (int i = 0; i < ids.Count; i++)
            if (!_selectedInstruments.Contains(ids[i])) _selectedInstruments.Add(ids[i]);
        Bump();
        SelectionChanged?.Invoke();
    }

    public void SelectMarker(DirgeId id, bool additive)
    {
        if (!additive)
        {
            _selectedInstruments.Clear();
            _selectedMarkers.Clear();
            SelectedTrack = DirgeId.None;
        }
        if (additive && _selectedMarkers.Contains(id)) _selectedMarkers.Remove(id);
        else if (!_selectedMarkers.Contains(id)) _selectedMarkers.Add(id);
        Bump();
        SelectionChanged?.Invoke();
    }

    public void SelectTrack(DirgeId id)
    {
        _selectedInstruments.Clear();
        _selectedMarkers.Clear();
        SelectedTrack = id;
        Bump();
        SelectionChanged?.Invoke();
    }

    public void SelectLane(DirgeId id)
    {
        if (SelectedLane == id) return;
        SelectedLane = id;
        Bump();
        SelectionChanged?.Invoke();
    }

    public void ClearSelection()
    {
        if (_selectedInstruments.Count == 0 && _selectedMarkers.Count == 0 && SelectedTrack.IsNone) return;
        _selectedInstruments.Clear();
        _selectedMarkers.Clear();
        SelectedTrack = DirgeId.None;
        Bump();
        SelectionChanged?.Invoke();
    }

    public bool IsSelected(Instrument instrument) => _selectedInstruments.Contains(instrument.Id);
    public bool IsSelected(LogicMarker marker) => _selectedMarkers.Contains(marker.Id);

    /// <summary>The single selected instrument, or null when none/many.</summary>
    public Instrument? PrimaryInstrument => _selectedInstruments.Count == 1 ? FindInstrument(_selectedInstruments[0]) : null;
    public LogicMarker? PrimaryMarker => _selectedMarkers.Count == 1 ? FindMarker(_selectedMarkers[0]) : null;

    // ---- lookups ------------------------------------------------------------------------------------------------

    public AudioEvent? FindEvent(DirgeId id)
    {
        if (id.IsNone) return null;
        List<AudioEvent> events = Project.Events;
        for (int i = 0; i < events.Count; i++) if (events[i].Id == id) return events[i];
        return null;
    }

    public Track? FindTrack(DirgeId id)
    {
        if (Event is null) return null;
        for (int i = 0; i < Event.Tracks.Count; i++) if (Event.Tracks[i].Id == id) return Event.Tracks[i];
        return null;
    }

    public Instrument? FindInstrument(DirgeId id) => FindInstrument(id, out _);

    public Instrument? FindInstrument(DirgeId id, out Track? track)
    {
        track = null;
        if (Event is null) return null;
        for (int t = 0; t < Event.Tracks.Count; t++)
        {
            List<Instrument> list = Event.Tracks[t].Instruments;
            for (int i = 0; i < list.Count; i++)
                if (list[i].Id == id) { track = Event.Tracks[t]; return list[i]; }
        }
        return null;
    }

    public LogicMarker? FindMarker(DirgeId id)
    {
        if (Event is null) return null;
        for (int i = 0; i < Event.Markers.Count; i++) if (Event.Markers[i].Id == id) return Event.Markers[i];
        return null;
    }

    /// <summary>Local parameter of the event or a global parameter.</summary>
    public ParameterDefinition? FindParameter(DirgeId id)
    {
        if (id.IsNone) return null;
        if (Event is not null)
            for (int i = 0; i < Event.LocalParameters.Count; i++) if (Event.LocalParameters[i].Id == id) return Event.LocalParameters[i];
        for (int i = 0; i < Project.GlobalParameters.Count; i++) if (Project.GlobalParameters[i].Id == id) return Project.GlobalParameters[i];
        return null;
    }

    public bool IsGlobalParameter(DirgeId id)
    {
        for (int i = 0; i < Project.GlobalParameters.Count; i++) if (Project.GlobalParameters[i].Id == id) return true;
        return false;
    }

    /// <summary>Automation lane on the event or on any of its instruments.</summary>
    public AutomationLane? FindLane(DirgeId id) => FindLane(id, out _);

    public AutomationLane? FindLane(DirgeId id, out Instrument? owner)
    {
        owner = null;
        if (Event is null) return null;
        for (int i = 0; i < Event.Automation.Count; i++) if (Event.Automation[i].Id == id) return Event.Automation[i];
        for (int t = 0; t < Event.Tracks.Count; t++)
        {
            List<Instrument> list = Event.Tracks[t].Instruments;
            for (int i = 0; i < list.Count; i++)
            {
                List<AutomationLane> lanes = InstrumentLanes(list[i]);
                for (int l = 0; l < lanes.Count; l++)
                    if (lanes[l].Id == id) { owner = list[i]; return lanes[l]; }
            }
        }
        return null;
    }

    /// <summary>Instrument-level automation: lanes stored on the owning event whose target is the instrument.</summary>
    public List<AutomationLane> InstrumentLanes(Instrument instrument)
    {
        var result = new List<AutomationLane>();
        if (Event is null) return result;
        for (int i = 0; i < Event.Automation.Count; i++)
        {
            PropertyRef target = Event.Automation[i].Target;
            if (target.Owner == instrument.Id && target.Kind is PropertyKind.InstrumentVolumeDb or PropertyKind.InstrumentPitch)
                result.Add(Event.Automation[i]);
        }
        return result;
    }

    public Bus? FindBus(DirgeId id)
    {
        for (int i = 0; i < Project.Mixer.Buses.Count; i++) if (Project.Mixer.Buses[i].Id == id) return Project.Mixer.Buses[i];
        return null;
    }

    public Snapshot? FindSnapshot(DirgeId id)
    {
        for (int i = 0; i < Project.Snapshots.Count; i++) if (Project.Snapshots[i].Id == id) return Project.Snapshots[i];
        return null;
    }

    public string ParameterName(DirgeId id) => FindParameter(id)?.Name ?? "(none)";
    public string EventPath(DirgeId id) => FindEvent(id)?.Path ?? "(none)";
    public string MarkerName(DirgeId id) => FindMarker(id) is { } m ? (m.Name.Length > 0 ? m.Name : m.GetType().Name) : "(none)";
}
