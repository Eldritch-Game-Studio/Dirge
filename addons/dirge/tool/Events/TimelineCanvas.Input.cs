#nullable enable
using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Runtime;
using Dirge.Tool.Core;
using Godot;

namespace Dirge.Tool.Events;

public sealed partial class TimelineCanvas
{
    private enum Mode { None, PendingDrag, DragMove, ResizeLeft, ResizeRight, RubberBand, MarkerMove, MarkerResize, TempoMove, Scrub }

    private Mode _mode;
    private Vector2 _pressPosition;
    private double _pressUnit;
    private Rect2 _band;
    private Instrument? _pressedInstrument;
    private Track? _pressedTrack;
    private LogicMarker? _pressedMarker;
    private TempoMarker? _pressedTempo;
    private float _pressedTempoOrigin;
    private object _dragToken = new();
    private readonly List<PlacementEdit> _placementEdits = new();
    private readonly List<MarkerEdit> _markerEdits = new();
    private readonly List<DirgeId> _bandHits = new();

    private PopupMenu _menu = null!;
    private double _menuUnit;
    private Track? _menuTrack;

    private const int MenuAddInstrumentBase = 100;
    private const int MenuAddMarkerBase = 200;
    private const int MenuAddTrack = 1;
    private const int MenuRemoveTrack = 2;
    private const int MenuDelete = 3;
    private const int MenuCopy = 4;
    private const int MenuPaste = 5;
    private const int MenuDuplicate = 6;
    private const int MenuAddTempo = 7;
    private const int MenuZoomFit = 8;
    private const int MenuSnap = 9;

    /// <summary>Owner-provided clipboard operations (shared with the pane's keyboard commands).</summary>
    public Action? CopyRequested { get; set; }
    public Action? PasteRequested { get; set; }
    public Action? DuplicateRequested { get; set; }
    public Action? DeleteRequested { get; set; }

    /// <summary>Unit (seconds or parameter value) under the last right-click or press; used as paste anchor.</summary>
    public double LastPointerUnit { get; private set; }
    /// <summary>Track under the last pointer press/right-click, or null.</summary>
    public Track? LastPointerTrack { get; private set; }

    // ---- snapping ------------------------------------------------------------------------------------------------------

    public double Snap(double unit)
    {
        if (!_state.SnapToGrid) return unit;
        if (!_state.Sheet.IsNone)
        {
            ParameterDefinition? p = SheetParameter;
            double step = p is { Kind: ParameterKind.Continuous } ? 0.1 : 1.0;
            return Math.Round(unit / step) * step;
        }
        AudioEvent? ev = _state.Event;
        if (ev is null || ev.Tempo.Count == 0) return Math.Round(unit * 10.0) / 10.0;
        List<TempoMarker> tempo = ev.Tempo;
        int segment = 0;
        for (int i = 0; i < tempo.Count; i++) if (tempo[i].Position <= unit) segment = i;
        TempoMarker marker = tempo[segment];
        double beat = 60.0 / Math.Max(1.0, marker.Bpm);
        double snapped = marker.Position + Math.Round((unit - marker.Position) / beat) * beat;
        if (segment + 1 < tempo.Count && snapped > tempo[segment + 1].Position) snapped = tempo[segment + 1].Position;
        return snapped;
    }

    private double ClampUnit(double unit)
    {
        if (_state.Sheet.IsNone) return Math.Max(0, unit);
        ParameterDefinition? p = SheetParameter;
        return p is null ? unit : Math.Clamp(unit, p.Minimum, p.Maximum);
    }

    // ---- hit testing -----------------------------------------------------------------------------------------------------

    private int TabAt(Vector2 p)
    {
        if (p.Y > TabsHeight) return -1;
        for (int i = 0; i < _tabs.Count; i++) if (_tabs[i].Rect.HasPoint(p)) return i;
        return -1;
    }

    private int BlockAt(Vector2 p)
    {
        for (int i = _blocks.Count - 1; i >= 0; i--) if (_blocks[i].Rect.HasPoint(p)) return i;
        return -1;
    }

    private int MarkerAt(Vector2 p)
    {
        if (p.Y < MarkerLaneTop || p.Y > MarkerLaneTop + MarkerLaneHeight) return -1;
        for (int i = _markers.Count - 1; i >= 0; i--)
        {
            MarkerGeom m = _markers[i];
            Rect2 r = m.Region ? m.Rect : new Rect2(m.Rect.Position.X - 2, m.Rect.Position.Y, Math.Max(m.Rect.Size.X, 40f * _scale), m.Rect.Size.Y);
            if (r.HasPoint(p)) return i;
        }
        return -1;
    }

    private int TempoAt(Vector2 p)
    {
        for (int i = _tempos.Count - 1; i >= 0; i--) if (_tempos[i].Rect.HasPoint(p)) return i;
        return -1;
    }

    private int RowAt(Vector2 p)
    {
        if (p.Y < TracksTop) return -1;
        for (int i = 0; i < _rows.Count; i++) if (p.Y >= _rows[i].Rect.Position.Y && p.Y < _rows[i].Rect.End.Y) return i;
        return -1;
    }

    private bool InRuler(Vector2 p) => p.X >= HeaderWidth && p.Y >= RulerTop && p.Y < RulerTop + RulerHeight + BeatsHeight;

    // ---- input ---------------------------------------------------------------------------------------------------------

    public override void _GuiInput(InputEvent @event)
    {
        if (_state.Event is null) return;
        EnsureLayout();
        switch (@event)
        {
            case InputEventMouseButton button:
                OnMouseButton(button);
                break;
            case InputEventMouseMotion motion:
                OnMouseMotion(motion);
                break;
        }
    }

    private void OnMouseButton(InputEventMouseButton e)
    {
        Vector2 p = e.Position;
        switch (e.ButtonIndex)
        {
            case MouseButton.WheelUp:
            case MouseButton.WheelDown:
                if (!e.Pressed) return;
                float direction = e.ButtonIndex == MouseButton.WheelUp ? 1f : -1f;
                if (e.CtrlPressed) ZoomAt(p.X, direction > 0 ? 1.15 : 1 / 1.15);
                else if (e.ShiftPressed) ScrollVertical(-direction * TrackHeight * 0.5f);
                else ScrollHorizontal(-direction * VisibleUnits * 0.1 * e.Factor);
                AcceptEvent();
                return;
            case MouseButton.Left:
                if (e.Pressed) OnLeftPress(p, e);
                else OnLeftRelease(p, e);
                AcceptEvent();
                return;
            case MouseButton.Right:
                if (e.Pressed) OpenMenu(p);
                AcceptEvent();
                return;
        }
    }

    private void OnLeftPress(Vector2 p, InputEventMouseButton e)
    {
        GrabFocus();
        _pressPosition = p;
        _pressUnit = UnitOf(p.X);
        LastPointerUnit = Snap(ClampUnit(_pressUnit));
        int row = RowAt(p);
        LastPointerTrack = row >= 0 ? _rows[row].Track : null;

        int tab = TabAt(p);
        if (tab >= 0) { _state.SetSheet(_tabs[tab].Parameter); QueueRedraw(); return; }

        if (InRuler(p))
        {
            int tempoIndex = TempoAt(p);
            if (tempoIndex >= 0 && _state.Sheet.IsNone)
            {
                _pressedTempo = _tempos[tempoIndex].Marker;
                _pressedTempoOrigin = _pressedTempo.Position;
                _dragToken = new object();
                _mode = Mode.TempoMove;
                return;
            }
            _mode = Mode.Scrub;
            ScrubTo(p.X);
            return;
        }

        if (_state.Sheet.IsNone)
        {
            int markerIndex = MarkerAt(p);
            if (markerIndex >= 0)
            {
                MarkerGeom m = _markers[markerIndex];
                if (!_state.IsSelected(m.Marker) || e.ShiftPressed) _state.SelectMarker(m.Marker.Id, e.ShiftPressed);
                _pressedMarker = m.Marker;
                bool resize = m.Region && p.X >= m.Rect.End.X - GripWidth;
                _mode = resize ? Mode.MarkerResize : Mode.MarkerMove;
                BeginMarkerDrag();
                QueueRedraw();
                return;
            }
            if (p.Y >= MarkerLaneTop && p.Y < TracksTop)
            {
                if (!e.ShiftPressed) _state.ClearSelection();
                _state.Cursor = LastPointerUnit;
                QueueRedraw();
                return;
            }
        }

        if (p.X < HeaderWidth)
        {
            if (row >= 0) _state.SelectTrack(_rows[row].Track.Id);
            QueueRedraw();
            return;
        }

        int blockIndex = BlockAt(p);
        if (blockIndex >= 0)
        {
            BlockGeom b = _blocks[blockIndex];
            if (!_state.IsSelected(b.Instrument) || e.ShiftPressed) _state.SelectInstrument(b.Instrument.Id, e.ShiftPressed);
            if (e.ShiftPressed && !_state.IsSelected(b.Instrument)) { QueueRedraw(); return; }
            _pressedInstrument = b.Instrument;
            _pressedTrack = b.Track;
            bool wide = b.Rect.Size.X > GripWidth * 3;
            if (wide && p.X <= b.Rect.Position.X + GripWidth) _mode = Mode.ResizeLeft;
            else if (wide && p.X >= b.Rect.End.X - GripWidth) _mode = Mode.ResizeRight;
            else _mode = Mode.PendingDrag;
            BeginPlacementDrag();
            QueueRedraw();
            return;
        }

        // Empty track area: rubber band; click sets the cursor.
        if (!e.ShiftPressed) _state.ClearSelection();
        _state.Cursor = LastPointerUnit;
        _band = new Rect2(p, Vector2.Zero);
        _mode = Mode.RubberBand;
        _bandAdditive = e.ShiftPressed;
        QueueRedraw();
    }

    private bool _bandAdditive;

    private void BeginPlacementDrag()
    {
        _dragToken = new object();
        _placementEdits.Clear();
        IReadOnlyList<DirgeId> selected = _state.SelectedInstruments;
        for (int i = 0; i < selected.Count; i++)
        {
            Instrument? inst = _state.FindInstrument(selected[i]);
            if (inst is null) continue;
            float length = inst.Placement.Length;
            _placementEdits.Add(new PlacementEdit(inst, inst.Placement.Start, length, inst.Placement.Start, length));
        }
    }

    private void BeginMarkerDrag()
    {
        _dragToken = new object();
        _markerEdits.Clear();
        IReadOnlyList<DirgeId> selected = _state.SelectedMarkers;
        for (int i = 0; i < selected.Count; i++)
        {
            LogicMarker? m = _state.FindMarker(selected[i]);
            if (m is null) continue;
            float length = EventEdits.RegionLength(m);
            _markerEdits.Add(new MarkerEdit(m, m.Position, length, m.Position, length));
        }
    }

    private void OnMouseMotion(InputEventMouseMotion e)
    {
        Vector2 p = e.Position;
        switch (_mode)
        {
            case Mode.None:
                UpdateCursorShape(p);
                return;
            case Mode.PendingDrag:
                if ((p - _pressPosition).Length() < 4f * _scale) return;
                _mode = Mode.DragMove;
                goto case Mode.DragMove;
            case Mode.DragMove:
                DragMove(p);
                return;
            case Mode.ResizeLeft:
            case Mode.ResizeRight:
                Resize(p, _mode == Mode.ResizeLeft);
                return;
            case Mode.RubberBand:
                _band = new Rect2(_pressPosition, p - _pressPosition);
                QueueRedraw();
                return;
            case Mode.MarkerMove:
            case Mode.MarkerResize:
                DragMarkers(p, _mode == Mode.MarkerResize);
                return;
            case Mode.TempoMove:
                DragTempo(p);
                return;
            case Mode.Scrub:
                ScrubTo(p.X);
                return;
        }
    }

    private void UpdateCursorShape(Vector2 p)
    {
        int block = BlockAt(p);
        if (block >= 0)
        {
            BlockGeom b = _blocks[block];
            bool grip = b.Rect.Size.X > GripWidth * 3 && (p.X <= b.Rect.Position.X + GripWidth || p.X >= b.Rect.End.X - GripWidth);
            MouseDefaultCursorShape = grip ? CursorShape.Hsize : CursorShape.Move;
            return;
        }
        MouseDefaultCursorShape = CursorShape.Arrow;
    }

    private void DragMove(Vector2 p)
    {
        if (_pressedInstrument is null || _placementEdits.Count == 0) return;
        double delta = UnitOf(p.X) - _pressUnit;
        // Snap the pressed block's start; move everything else by the same delta.
        float origin = 0;
        for (int i = 0; i < _placementEdits.Count; i++) if (ReferenceEquals(_placementEdits[i].Instrument, _pressedInstrument)) origin = _placementEdits[i].OldStart;
        double snappedStart = ClampUnit(Snap(origin + delta));
        delta = snappedStart - origin;
        // Keep every block inside the sheet/timeline.
        for (int i = 0; i < _placementEdits.Count; i++)
        {
            double min = _state.Sheet.IsNone ? 0 : SheetParameter?.Minimum ?? double.NegativeInfinity;
            if (_placementEdits[i].OldStart + delta < min) delta = min - _placementEdits[i].OldStart;
        }
        for (int i = 0; i < _placementEdits.Count; i++)
        {
            PlacementEdit edit = _placementEdits[i];
            _placementEdits[i] = edit with { NewStart = (float)(edit.OldStart + delta), NewLength = edit.OldLength };
        }
        _edits.SetPlacements(_dragToken, _placementEdits.Count == 1 ? "Move instrument" : "Move instruments", _placementEdits);
    }

    private void Resize(Vector2 p, bool left)
    {
        if (_pressedInstrument is null || _placementEdits.Count == 0) return;
        double unit = ClampUnit(Snap(UnitOf(p.X)));
        const float minLength = 0.01f;
        for (int i = 0; i < _placementEdits.Count; i++)
        {
            PlacementEdit edit = _placementEdits[i];
            if (!ReferenceEquals(edit.Instrument, _pressedInstrument)) continue;
            float oldLength = edit.OldLength > 0 ? edit.OldLength : (float)(_state.Sheet.IsNone ? _lengths.NaturalLength(edit.Instrument) : 0.1);
            float end = edit.OldStart + oldLength;
            if (left)
            {
                float start = (float)Math.Min(unit, end - minLength);
                _placementEdits[i] = edit with { NewStart = start, NewLength = end - start };
            }
            else
            {
                float length = (float)Math.Max(minLength, unit - edit.OldStart);
                _placementEdits[i] = edit with { NewStart = edit.OldStart, NewLength = length };
            }
        }
        _edits.SetPlacements(_dragToken, "Resize instrument", _placementEdits);
    }

    private void DragMarkers(Vector2 p, bool resize)
    {
        if (_pressedMarker is null || _markerEdits.Count == 0) return;
        double unit = ClampUnit(Snap(UnitOf(p.X)));
        if (resize)
        {
            for (int i = 0; i < _markerEdits.Count; i++)
            {
                MarkerEdit edit = _markerEdits[i];
                if (!ReferenceEquals(edit.Marker, _pressedMarker)) continue;
                _markerEdits[i] = edit with { NewPosition = edit.OldPosition, NewLength = (float)Math.Max(0.01, unit - edit.OldPosition) };
            }
            _edits.SetMarkerPositions(_dragToken, "Resize region", _markerEdits);
            return;
        }
        float origin = _pressedMarker.Position;
        for (int i = 0; i < _markerEdits.Count; i++) if (ReferenceEquals(_markerEdits[i].Marker, _pressedMarker)) origin = _markerEdits[i].OldPosition;
        double delta = ClampUnit(Snap(origin + (UnitOf(p.X) - _pressUnit))) - origin;
        for (int i = 0; i < _markerEdits.Count; i++) if (_markerEdits[i].OldPosition + delta < 0) delta = -_markerEdits[i].OldPosition;
        for (int i = 0; i < _markerEdits.Count; i++)
        {
            MarkerEdit edit = _markerEdits[i];
            _markerEdits[i] = edit with { NewPosition = (float)(edit.OldPosition + delta), NewLength = edit.OldLength };
        }
        _edits.SetMarkerPositions(_dragToken, "Move marker", _markerEdits);
    }

    private void DragTempo(Vector2 p)
    {
        if (_pressedTempo is null) return;
        float position = (float)Math.Max(0, Snap(UnitOf(p.X)));
        TempoMarker marker = _pressedTempo;
        AudioEvent ev = _state.Event!;
        _edits.SetContinuous(_dragToken, "Move tempo marker", _pressedTempoOrigin, position, v =>
        {
            marker.Position = v;
            ev.Tempo.Sort(static (a, b) => a.Position.CompareTo(b.Position));
        });
    }

    private void ScrubTo(float x)
    {
        double unit = ClampUnit(UnitOf(x));
        if (_state.Sheet.IsNone)
        {
            _state.Cursor = Snap(unit);
            IReadOnlyList<EventInstance> instances = _audition.Instances;
            for (int i = 0; i < instances.Count; i++)
            {
                EventInstance instance = instances[i];
                if (instance.Description.Id == _state.EventId && instance.State != PlaybackState.Stopped) instance.TimelinePosition = unit;
            }
        }
        else
        {
            _state.Cursor = unit;
            ParameterDefinition? parameter = SheetParameter;
            if (parameter is not null)
            {
                float value = (float)unit;
                IReadOnlyList<EventInstance> instances = _audition.Instances;
                bool any = false;
                for (int i = 0; i < instances.Count; i++)
                {
                    EventInstance instance = instances[i];
                    if (instance.Description.Id != _state.EventId || instance.State == PlaybackState.Stopped) continue;
                    instance.SetParameter(parameter.Name, value, immediate: true);
                    any = true;
                }
                if (!any && _state.IsGlobalParameter(parameter.Id)) _audition.System?.SetGlobalParameter(parameter.Name, value, immediate: true);
            }
        }
        QueueRedraw();
    }

    private void OnLeftRelease(Vector2 p, InputEventMouseButton e)
    {
        Mode mode = _mode;
        _mode = Mode.None;
        switch (mode)
        {
            case Mode.DragMove:
                _edits.EndContinuous();
                // Vertical move: single instrument dropped on another track.
                if (_pressedInstrument is not null && _pressedTrack is not null && _placementEdits.Count == 1)
                {
                    int row = RowAt(p);
                    if (row >= 0 && !ReferenceEquals(_rows[row].Track, _pressedTrack)) _edits.MoveInstrumentToTrack(_pressedInstrument, _pressedTrack, _rows[row].Track);
                }
                break;
            case Mode.ResizeLeft:
            case Mode.ResizeRight:
            case Mode.MarkerMove:
            case Mode.MarkerResize:
            case Mode.TempoMove:
                _edits.EndContinuous();
                break;
            case Mode.RubberBand:
                CompleteRubberBand();
                break;
        }
        _pressedInstrument = null;
        _pressedTrack = null;
        _pressedMarker = null;
        _pressedTempo = null;
        QueueRedraw();
    }

    private void CompleteRubberBand()
    {
        Rect2 band = _band.Abs();
        if (band.Size.X < 2 && band.Size.Y < 2) return;
        _bandHits.Clear();
        for (int i = 0; i < _blocks.Count; i++) if (_blocks[i].Rect.Intersects(band)) _bandHits.Add(_blocks[i].Instrument.Id);
        if (_bandHits.Count > 0) _state.SelectInstruments(_bandHits, _bandAdditive);
        for (int i = 0; i < _markers.Count; i++) if (_markers[i].Rect.Intersects(band)) _state.SelectMarker(_markers[i].Marker.Id, true);
    }

    // ---- view operations -----------------------------------------------------------------------------------------------

    public void ZoomAt(float x, double factor)
    {
        double anchor = UnitOf(x);
        _zoom = Math.Clamp(_zoom * factor, 2.0, 20000.0);
        _scrollX = anchor - (x - HeaderWidth) / _zoom;
        ClampScroll();
        QueueRedraw();
    }

    public void ScrollHorizontal(double deltaUnits)
    {
        _scrollX += deltaUnits;
        ClampScroll();
        QueueRedraw();
    }

    public void ScrollVertical(float deltaPixels)
    {
        float max = Math.Max(0, _contentHeight - (Size.Y - TracksTop));
        _scrollY = Math.Clamp(_scrollY + deltaPixels, 0, max);
        QueueRedraw();
    }

    private void ClampScroll()
    {
        if (_state.Sheet.IsNone) { if (_scrollX < 0) _scrollX = 0; return; }
        ParameterDefinition? p = SheetParameter;
        if (p is null) return;
        double max = Math.Max(p.Minimum, p.Maximum - VisibleUnits);
        _scrollX = Math.Clamp(_scrollX, p.Minimum, max);
    }

    /// <summary>Zoom to show the whole timeline (or the parameter range).</summary>
    public void ZoomToFit()
    {
        if (!_state.Sheet.IsNone) { FitSheet(); QueueRedraw(); return; }
        double length = Math.Max(1.0, _lengths.EventLength(_state.Event) * 1.05);
        _zoom = Math.Max(2.0, (Size.X - HeaderWidth) / length);
        _scrollX = 0;
        QueueRedraw();
    }

    private void FitSheet()
    {
        ParameterDefinition? p = SheetParameter;
        if (p is null || Size.X <= HeaderWidth) return;
        double range = Math.Max(1e-3, p.Maximum - p.Minimum);
        _zoom = (Size.X - HeaderWidth - 8) / range;
        _scrollX = p.Minimum;
    }

    // ---- drag & drop from the browser -----------------------------------------------------------------------------------

    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        if (_state.Event is null) return false;
        DragPayload? payload = ToolDrag.Unwrap(data);
        return payload is AssetDrag || payload is EventDrag drag && drag.Event != _state.EventId;
    }

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        AudioEvent? ev = _state.Event;
        DragPayload? payload = ToolDrag.Unwrap(data);
        if (ev is null || payload is null) return;
        EnsureLayout();
        double unit = ClampUnit(Snap(UnitOf(Math.Max(HeaderWidth, atPosition.X))));
        int row = RowAt(atPosition);
        if (payload is AssetDrag asset)
        {
            int block = BlockAt(atPosition);
            if (block >= 0 && _blocks[block].Instrument is MultiInstrument multi)
            {
                _edits.ListAdd("Add multi entry", multi.Entries, new MultiEntry { Asset = new AssetRef(asset.Path) });
                return;
            }
            if (block >= 0 && _blocks[block].Instrument is ScattererInstrument scatterer)
            {
                _edits.ListAdd("Add scatterer entry", scatterer.Entries, new MultiEntry { Asset = new AssetRef(asset.Path) });
                return;
            }
            Track track = row >= 0 ? _rows[row].Track : ev.Tracks.Count > 0 ? ev.Tracks[^1] : _edits.AddTrack();
            var clip = new ClipInstrument { Name = asset.Path.GetFile().GetBaseName(), Asset = new AssetRef(asset.Path) };
            clip.Placement.Parameter = _state.Sheet;
            clip.Placement.Start = (float)unit;
            clip.Placement.Length = _state.Sheet.IsNone ? 0f : DefaultSheetLength();
            _edits.AddInstrument(track, clip);
            _state.SelectInstrument(clip.Id, false);
        }
        else if (payload is EventDrag eventDrag)
        {
            Track track = row >= 0 ? _rows[row].Track : ev.Tracks.Count > 0 ? ev.Tracks[^1] : _edits.AddTrack();
            var nested = new EventInstrument { Name = _state.EventPath(eventDrag.Event).GetFile(), Event = eventDrag.Event };
            nested.Placement.Parameter = _state.Sheet;
            nested.Placement.Start = (float)unit;
            nested.Placement.Length = _state.Sheet.IsNone ? 0f : DefaultSheetLength();
            _edits.AddInstrument(track, nested);
            _state.SelectInstrument(nested.Id, false);
        }
    }

    private float DefaultSheetLength()
    {
        ParameterDefinition? p = SheetParameter;
        if (p is null) return 1f;
        return p.Kind == ParameterKind.Continuous ? (p.Maximum - p.Minimum) * 0.25f : 1f;
    }

    // ---- context menu --------------------------------------------------------------------------------------------------

    private void BuildMenu()
    {
        _menu = new PopupMenu();
        AddChild(_menu);
        _menu.IdPressed += OnMenuId;
    }

    private void OpenMenu(Vector2 p)
    {
        _menuUnit = ClampUnit(Snap(UnitOf(Math.Max(HeaderWidth, p.X))));
        int row = RowAt(p);
        _menuTrack = row >= 0 ? _rows[row].Track : null;
        LastPointerUnit = _menuUnit;
        LastPointerTrack = _menuTrack;
        _state.Cursor = _menuUnit;

        int block = BlockAt(p);
        if (block >= 0 && !_state.IsSelected(_blocks[block].Instrument)) _state.SelectInstrument(_blocks[block].Instrument.Id, false);
        int marker = MarkerAt(p);
        if (marker >= 0 && !_state.IsSelected(_markers[marker].Marker)) _state.SelectMarker(_markers[marker].Marker.Id, false);

        _menu.Clear();
        var sub = new PopupMenu { Name = "AddInstrument" };
        foreach (InstrumentKind kind in Enum.GetValues<InstrumentKind>()) sub.AddItem(kind.ToString(), MenuAddInstrumentBase + (int)kind);
        ReplaceSubmenu(sub);
        _menu.AddSubmenuNodeItem("Add instrument", sub);
        if (_state.Sheet.IsNone)
        {
            var markers = new PopupMenu { Name = "AddMarker" };
            foreach (MarkerKind kind in Enum.GetValues<MarkerKind>()) markers.AddItem(EventEdits.MarkerLabel(EventEdits.CreateMarker(kind, 0, 1)), MenuAddMarkerBase + (int)kind);
            ReplaceSubmenu(markers);
            _menu.AddSubmenuNodeItem("Add marker", markers);
            _menu.AddItem("Add tempo marker here", MenuAddTempo);
        }
        _menu.AddSeparator();
        _menu.AddItem("Add track", MenuAddTrack);
        if (_menuTrack is not null) _menu.AddItem($"Remove track \"{_menuTrack.Name}\"", MenuRemoveTrack);
        _menu.AddSeparator();
        bool hasSelection = _state.SelectedInstruments.Count > 0 || _state.SelectedMarkers.Count > 0;
        _menu.AddItem("Copy", MenuCopy);
        _menu.SetItemDisabled(_menu.GetItemIndex(MenuCopy), !hasSelection);
        _menu.AddItem("Paste", MenuPaste);
        _menu.AddItem("Duplicate", MenuDuplicate);
        _menu.SetItemDisabled(_menu.GetItemIndex(MenuDuplicate), !hasSelection);
        _menu.AddItem("Delete", MenuDelete);
        _menu.SetItemDisabled(_menu.GetItemIndex(MenuDelete), !hasSelection);
        _menu.AddSeparator();
        _menu.AddCheckItem("Snap to grid", MenuSnap);
        _menu.SetItemChecked(_menu.GetItemIndex(MenuSnap), _state.SnapToGrid);
        _menu.AddItem("Zoom to fit", MenuZoomFit);

        _menu.Position = (Vector2I)(GetScreenPosition() + p);
        _menu.Popup();
        QueueRedraw();
    }

    private void ReplaceSubmenu(PopupMenu sub)
    {
        Node? old = _menu.GetNodeOrNull(sub.Name.ToString());
        old?.QueueFree();
        if (old is not null) _menu.RemoveChild(old);
        _menu.AddChild(sub);
    }

    private void OnMenuId(long id)
    {
        AudioEvent? ev = _state.Event;
        if (ev is null) return;
        if (id >= MenuAddInstrumentBase && id < MenuAddInstrumentBase + 100)
        {
            var kind = (InstrumentKind)(id - MenuAddInstrumentBase);
            Track track = _menuTrack ?? (ev.Tracks.Count > 0 ? ev.Tracks[^1] : _edits.AddTrack());
            Instrument instrument = EventEdits.CreateInstrument(kind, _state.Sheet, (float)_menuUnit, _state.Sheet.IsNone ? 0f : DefaultSheetLength());
            if (instrument is SilenceInstrument && _state.Sheet.IsNone) instrument.Placement.Length = 1f;
            _edits.AddInstrument(track, instrument);
            _state.SelectInstrument(instrument.Id, false);
            return;
        }
        if (id >= MenuAddMarkerBase && id < MenuAddMarkerBase + 100)
        {
            var kind = (MarkerKind)(id - MenuAddMarkerBase);
            float length = (float)Math.Max(0.25, _hasTempo ? 4 * 60.0 / Math.Max(1, ev.Tempo[0].Bpm) : 2.0);
            LogicMarker marker = EventEdits.CreateMarker(kind, (float)_menuUnit, length);
            _edits.AddMarker(marker);
            _state.SelectMarker(marker.Id, false);
            return;
        }
        switch ((int)id)
        {
            case MenuAddTrack:
                _state.SelectTrack(_edits.AddTrack().Id);
                break;
            case MenuRemoveTrack:
                if (_menuTrack is not null) { _edits.RemoveTrack(_menuTrack); _state.Refresh(); }
                break;
            case MenuAddTempo:
                _edits.AddTempoMarker(new TempoMarker { Position = (float)_menuUnit, Bpm = ev.Tempo.Count > 0 ? ev.Tempo[^1].Bpm : 120f, BeatsPerBar = ev.Tempo.Count > 0 ? ev.Tempo[^1].BeatsPerBar : 4 });
                break;
            case MenuDelete: DeleteRequested?.Invoke(); break;
            case MenuCopy: CopyRequested?.Invoke(); break;
            case MenuPaste: PasteRequested?.Invoke(); break;
            case MenuDuplicate: DuplicateRequested?.Invoke(); break;
            case MenuSnap: _state.SnapToGrid = !_state.SnapToGrid; _state.Bump(); break;
            case MenuZoomFit: ZoomToFit(); break;
        }
        QueueRedraw();
    }
}
