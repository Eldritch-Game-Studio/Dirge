#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Dirge.Project;
using Dirge.Runtime;
using Dirge.Tool.Core;
using Godot;

namespace Dirge.Tool.Events;

/// <summary>
/// Precision canvas of the event editor: sheet tabs, ruler (seconds + beats/bars), marker lane, tracks with
/// instrument blocks, playhead. Geometry is computed once per (content revision, size, zoom, scroll) into reusable
/// lists; <see cref="_Draw"/> only iterates them. Input handling lives in the <c>TimelineCanvas.Input.cs</c> part.
/// </summary>
public sealed partial class TimelineCanvas : Control
{
    // ---- metrics (unscaled pixels; multiplied by the editor scale at layout time) ----------------------------------
    private const float TabsHeightBase = 24f;
    private const float RulerHeightBase = 26f;
    private const float BeatsHeightBase = 14f;
    private const float MarkerLaneHeightBase = 22f;
    private const float TrackHeightBase = 64f;
    private const float HeaderWidthBase = 150f;
    private const float GripWidthBase = 6f;
    private const float MinBlockWidthBase = 8f;

    private readonly EventEditorState _state;
    private readonly EventEdits _edits;
    private readonly AssetLengthCache _lengths;
    private readonly IAudition _audition;

    private float _scale = 1f;
    private float TabsHeight => TabsHeightBase * _scale;
    private float RulerHeight => RulerHeightBase * _scale;
    private float BeatsHeight => _hasTempo && _state.Sheet.IsNone ? BeatsHeightBase * _scale : 0f;
    private float MarkerLaneHeight => _state.Sheet.IsNone ? MarkerLaneHeightBase * _scale : 0f;
    private float TrackHeight => TrackHeightBase * _scale;
    private float HeaderWidth => HeaderWidthBase * _scale;
    private float GripWidth => GripWidthBase * _scale;
    private float TracksTop => TabsHeight + RulerHeight + BeatsHeight + MarkerLaneHeight;
    private float RulerTop => TabsHeight;
    private float MarkerLaneTop => TabsHeight + RulerHeight + BeatsHeight;

    // ---- view --------------------------------------------------------------------------------------------------------
    /// <summary>Pixels per unit (second or parameter unit).</summary>
    private double _zoom = 120.0;
    /// <summary>Leftmost visible unit.</summary>
    private double _scrollX;
    private float _scrollY;
    private bool _hasTempo;
    private DirgeId _viewSheet = DirgeId.None;
    private double _timelineZoom = 120.0;
    private double _timelineScrollX;

    // ---- cached geometry ---------------------------------------------------------------------------------------------
    private readonly List<TabGeom> _tabs = new();
    private readonly List<RowGeom> _rows = new();
    private readonly List<BlockGeom> _blocks = new();
    private readonly List<MarkerGeom> _markers = new();
    private readonly List<TempoGeom> _tempos = new();
    private readonly List<TickGeom> _ticks = new();
    private readonly List<BeatGeom> _beats = new();
    private int _layoutRevision = -1;
    private Vector2 _layoutSize;
    private double _layoutZoom;
    private double _layoutScrollX;
    private float _layoutScrollY;
    private float _layoutScale;
    private string _sheetTitle = "";
    private float _contentHeight;

    // ---- playback ------------------------------------------------------------------------------------------------------
    private double _playhead = -1;
    private double _sheetValue = double.NaN;

    private Font _font = null!;
    private int _fontSize = 12;

    public TimelineCanvas(EventEditorState state, EventEdits edits, AssetLengthCache lengths, IAudition audition)
    {
        _state = state;
        _edits = edits;
        _lengths = lengths;
        _audition = audition;
        ClipContents = true;
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(320, 160);
    }

    public override void _Ready()
    {
        _font = GetThemeDefaultFont();
        _fontSize = Math.Clamp(GetThemeDefaultFontSize() - 3, 10, 14);
        _scale = EditorScale();
        BuildMenu();
        SetProcess(true);
    }

    private static float EditorScale()
    {
#if TOOLS
        if (Engine.IsEditorHint() && EditorInterface.Singleton is { } editor) return (float)editor.GetEditorScale();
#endif
        return 1f;
    }

    /// <summary>Call after any change of the edited event's content or view selection.</summary>
    public void Invalidate() => QueueRedraw();

    public override void _Notification(int what)
    {
        if (what == NotificationResized) QueueRedraw();
    }

    // ---- coordinate mapping --------------------------------------------------------------------------------------------

    private float XOf(double unit) => HeaderWidth + (float)((unit - _scrollX) * _zoom);
    private double UnitOf(float x) => (x - HeaderWidth) / _zoom + _scrollX;

    private ParameterDefinition? SheetParameter => _state.FindParameter(_state.Sheet);

    private double VisibleUnits => Math.Max(1e-6, (Size.X - HeaderWidth) / _zoom);

    // ---- layout ------------------------------------------------------------------------------------------------------

    private void EnsureLayout()
    {
        _scale = EditorScale();
        AudioEvent? ev = _state.Event;
        _hasTempo = ev is not null && ev.Tempo.Count > 0;
        if (_viewSheet != _state.Sheet)
        {
            // Each sheet keeps the timeline's zoom/scroll out of the way: sheets always open fitted to the parameter range.
            if (_viewSheet.IsNone) { _timelineZoom = _zoom; _timelineScrollX = _scrollX; }
            _viewSheet = _state.Sheet;
            if (_viewSheet.IsNone) { _zoom = _timelineZoom; _scrollX = _timelineScrollX; }
            else FitSheet();
            _scrollY = 0;
        }
        if (_layoutRevision == _state.Revision && _layoutSize == Size && _layoutZoom == _zoom && _layoutScrollX == _scrollX
            && _layoutScrollY == _scrollY && _layoutScale == _scale)
            return;
        _layoutRevision = _state.Revision;
        _layoutSize = Size;
        _layoutZoom = _zoom;
        _layoutScrollX = _scrollX;
        _layoutScrollY = _scrollY;
        _layoutScale = _scale;

        _tabs.Clear(); _rows.Clear(); _blocks.Clear(); _markers.Clear(); _tempos.Clear(); _ticks.Clear(); _beats.Clear();
        if (ev is null) return;

        LayoutTabs(ev);
        LayoutRuler();
        if (_state.Sheet.IsNone) { LayoutMarkers(ev); LayoutTempos(ev); }
        LayoutTracks(ev);
    }

    private void LayoutTabs(AudioEvent ev)
    {
        float x = 0;
        AddTab("Timeline", DirgeId.None, ref x);
        for (int i = 0; i < ev.LocalParameters.Count; i++) AddTab(ev.LocalParameters[i].Name, ev.LocalParameters[i].Id, ref x);
        List<ParameterDefinition> globals = _state.Project.GlobalParameters;
        for (int i = 0; i < globals.Count; i++) AddTab(globals[i].Name, globals[i].Id, ref x, global: true);
        _sheetTitle = _state.Sheet.IsNone ? "Timeline" : SheetParameter?.Name ?? "";
    }

    private void AddTab(string label, DirgeId id, ref float x, bool global = false)
    {
        float width = _font.GetStringSize(label, HorizontalAlignment.Left, -1, _fontSize).X + 20f * _scale + (global ? 14f * _scale : 0f);
        _tabs.Add(new TabGeom(new Rect2(x, 0, width, TabsHeight), label, id, global, id == _state.Sheet));
        x += width + 2f * _scale;
    }

    private void LayoutRuler()
    {
        double visible = VisibleUnits;
        double step = NiceStep(visible, Math.Max(1, (Size.X - HeaderWidth) / (70f * _scale)));
        bool onSheet = !_state.Sheet.IsNone;
        ParameterDefinition? parameter = SheetParameter;
        double first = Math.Floor(_scrollX / step) * step;
        double last = _scrollX + visible + step;
        int guard = 0;
        for (double u = first; u <= last && guard < 512; u += step, guard++)
        {
            float x = XOf(u);
            if (x < HeaderWidth - 1) continue;
            string label = onSheet ? FormatValue(u, parameter) : FormatTime(u);
            _ticks.Add(new TickGeom(x, label));
        }
        if (!onSheet && _hasTempo) LayoutBeats(_state.Event!, visible);
    }

    private void LayoutBeats(AudioEvent ev, double visible)
    {
        List<TempoMarker> tempo = ev.Tempo;
        double viewEnd = _scrollX + visible;
        int barNumber = 1;
        for (int i = 0; i < tempo.Count && _beats.Count < 2048; i++)
        {
            TempoMarker marker = tempo[i];
            double segmentStart = marker.Position;
            double segmentEnd = i + 1 < tempo.Count ? tempo[i + 1].Position : Math.Max(viewEnd, segmentStart);
            double beatLength = 60.0 / Math.Max(1.0, marker.Bpm);
            int beatsPerBar = Math.Max(1, marker.BeatsPerBar);
            // Skip beats far before the viewport but keep the bar count consistent.
            if (beatLength * _zoom < 3) continue; // too dense to draw
            int beatIndex = 0;
            if (_scrollX > segmentStart)
            {
                beatIndex = (int)Math.Floor((_scrollX - segmentStart) / beatLength);
                beatIndex -= beatIndex % beatsPerBar;
            }
            for (double t = segmentStart + beatIndex * beatLength; t < segmentEnd && t <= viewEnd && _beats.Count < 2048; t += beatLength, beatIndex++)
            {
                bool bar = beatIndex % beatsPerBar == 0;
                int barAt = barNumber + beatIndex / beatsPerBar;
                _beats.Add(new BeatGeom(XOf(t), bar, bar ? barAt.ToString(CultureInfo.InvariantCulture) : null));
            }
            barNumber += (int)Math.Ceiling((segmentEnd - segmentStart) / (beatLength * beatsPerBar));
        }
    }

    private void LayoutMarkers(AudioEvent ev)
    {
        float top = MarkerLaneTop;
        float h = MarkerLaneHeight;
        for (int i = 0; i < ev.Markers.Count; i++)
        {
            LogicMarker m = ev.Markers[i];
            float x = XOf(m.Position);
            float length = EventEdits.RegionLength(m);
            bool region = EventEdits.IsRegion(m);
            float w = region ? Math.Max(MinBlockWidthBase * _scale, (float)(length * _zoom)) : 10f * _scale;
            var rect = new Rect2(x, top, w, h);
            string label = m.Name.Length > 0 ? m.Name : EventEdits.MarkerLabel(m);
            _markers.Add(new MarkerGeom(rect, m, label, MarkerColor(m), region, _state.IsSelected(m)));
        }
    }

    private void LayoutTempos(AudioEvent ev)
    {
        for (int i = 0; i < ev.Tempo.Count; i++)
        {
            TempoMarker t = ev.Tempo[i];
            string label = string.Concat(t.Bpm.ToString("0.#", CultureInfo.InvariantCulture), " bpm ", t.BeatsPerBar.ToString(CultureInfo.InvariantCulture), "/4");
            float w = _font.GetStringSize(label, HorizontalAlignment.Left, -1, _fontSize - 2).X + 8f * _scale;
            _tempos.Add(new TempoGeom(new Rect2(XOf(t.Position) + 16f * _scale, RulerTop + RulerHeight, w, BeatsHeight), t, label));
        }
    }

    private void LayoutTracks(AudioEvent ev)
    {
        float y = TracksTop - _scrollY;
        DirgeId sheet = _state.Sheet;
        for (int t = 0; t < ev.Tracks.Count; t++)
        {
            Track track = ev.Tracks[t];
            var rowRect = new Rect2(0, y, Size.X, TrackHeight);
            string volume = track.VolumeDb.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " dB";
            _rows.Add(new RowGeom(rowRect, track, track.Name.Length > 0 ? track.Name : "Track", volume, track.Id == _state.SelectedTrack));
            for (int i = 0; i < track.Instruments.Count; i++)
            {
                Instrument inst = track.Instruments[i];
                if (inst.Placement.Parameter != sheet) continue;
                double length = inst.Placement.Length > 0 ? inst.Placement.Length
                    : sheet.IsNone ? _lengths.NaturalLength(inst) : 0;
                float x = XOf(inst.Placement.Start);
                float w = Math.Max(MinBlockWidthBase * _scale, (float)(length * _zoom));
                var rect = new Rect2(x, y + 4f * _scale, w, TrackHeight - 8f * _scale);
                _blocks.Add(new BlockGeom(rect, inst, track, inst.Name.Length > 0 ? inst.Name : EventEdits.TypeLabel(inst),
                    SubLabel(inst), InstrumentColor(inst), _state.IsSelected(inst), inst.Placement.Length <= 0 && sheet.IsNone));
            }
            y += TrackHeight;
        }
        _contentHeight = ev.Tracks.Count * TrackHeight;
    }

    private static string SubLabel(Instrument inst) => inst switch
    {
        ClipInstrument c => c.Asset.IsEmpty ? "(no asset)" : c.Asset.Path.GetFile(),
        MultiInstrument m => m.Entries.Count == 1 ? "1 entry" : m.Entries.Count.ToString(CultureInfo.InvariantCulture) + " entries",
        ScattererInstrument s => (s.Entries.Count == 1 ? "1 entry" : s.Entries.Count.ToString(CultureInfo.InvariantCulture) + " entries") + " · scatter",
        EventInstrument => "event",
        ProgrammerInstrument p => p.Key.Length > 0 ? "key: " + p.Key : "programmer",
        CommandInstrument c => c.Action.ToString(),
        SnapshotInstrument => "snapshot",
        SilenceInstrument => "silence",
        _ => "",
    };

    private static double NiceStep(double visibleUnits, double maxTicks)
    {
        double raw = visibleUnits / Math.Max(1, maxTicks);
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-9))));
        double normalized = raw / magnitude;
        double nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return nice * magnitude;
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        int minutes = (int)(seconds / 60);
        double rest = seconds - minutes * 60;
        return minutes > 0
            ? string.Concat(minutes.ToString(CultureInfo.InvariantCulture), ":", rest.ToString("00.##", CultureInfo.InvariantCulture))
            : rest.ToString("0.##", CultureInfo.InvariantCulture) + " s";
    }

    private static string FormatValue(double value, ParameterDefinition? parameter)
    {
        if (parameter is { Kind: ParameterKind.Labeled } p)
        {
            int index = (int)Math.Round(value);
            if (Math.Abs(value - index) < 1e-6 && index >= 0 && index < p.Labels.Count) return p.Labels[index];
        }
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    // ---- colors ------------------------------------------------------------------------------------------------------

    private static readonly Color Background = new("1b1c20");
    private static readonly Color PanelColor = new("24262b");
    private static readonly Color HeaderColor = new("2a2d33");
    private static readonly Color BorderColor = new("3a3d45");
    private static readonly Color TextColor = new("d8dae0");
    private static readonly Color MutedColor = new("8a8f9a");
    private static readonly Color AccentColor = new("f08a3c");
    private static readonly Color SelectionColor = new("ffffff");
    private static readonly Color PlayheadColor = new("ff4b4b");
    private static readonly Color CursorColor = new("9ad0ff");
    private static readonly Color GridColor = new("2f3238");
    private static readonly Color BeatColor = new("3b3f48");
    private static readonly Color BarColor = new("565b66");
    private static readonly Color BandColor = new(0.6f, 0.75f, 1f, 0.2f);

    private static Color Alpha(Color color, float alpha) => new(color.R, color.G, color.B, alpha);

    private static Color InstrumentColor(Instrument inst) => inst switch
    {
        ClipInstrument => new Color("3d7bd9"),
        MultiInstrument => new Color("8a5bd6"),
        ScattererInstrument => new Color("3fa36a"),
        SilenceInstrument => new Color("5c6068"),
        EventInstrument => new Color("e0853a"),
        ProgrammerInstrument => new Color("2fa8a3"),
        CommandInstrument => new Color("c9b43a"),
        SnapshotInstrument => new Color("d65b9b"),
        _ => new Color("777777"),
    };

    private static Color MarkerColor(LogicMarker m) => m switch
    {
        LoopRegion => new Color("4fb0ff"),
        TransitionMarker => new Color("ffb347"),
        TransitionRegion => new Color("ffb347"),
        SustainPoint => new Color("ff6b6b"),
        DestinationMarker => new Color("7ee787"),
        MagnetRegion => new Color("d2a8ff"),
        _ => new Color("aaaaaa"),
    };

    // ---- drawing -----------------------------------------------------------------------------------------------------

    public override void _Draw()
    {
        EnsureLayout();
        Vector2 size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), Background);
        if (_state.Event is null)
        {
            DrawString(_font, new Vector2(12f * _scale, size.Y * 0.5f), "Select an event in the browser to edit it.", HorizontalAlignment.Left, -1, _fontSize, MutedColor);
            return;
        }

        DrawTracks();
        DrawBlocks();
        DrawHeaderColumn();
        DrawRuler();
        if (_state.Sheet.IsNone) { DrawMarkerLane(); DrawTempos(); }
        DrawTabs();
        DrawCursor();
        DrawPlayhead();
        if (_mode == Mode.RubberBand) DrawRect(_band.Abs(), BandColor);
    }

    private void DrawTabs()
    {
        DrawRect(new Rect2(0, 0, Size.X, TabsHeight), HeaderColor);
        for (int i = 0; i < _tabs.Count; i++)
        {
            TabGeom tab = _tabs[i];
            DrawRect(tab.Rect, tab.Active ? PanelColor : HeaderColor);
            if (tab.Active) DrawRect(new Rect2(tab.Rect.Position.X, tab.Rect.End.Y - 2f * _scale, tab.Rect.Size.X, 2f * _scale), AccentColor);
            float textX = tab.Rect.Position.X + 10f * _scale;
            if (tab.Global)
            {
                DrawString(_font, new Vector2(textX, TabsHeight * 0.5f + _fontSize * 0.35f), "G", HorizontalAlignment.Left, -1, _fontSize, AccentColor);
                textX += 14f * _scale;
            }
            DrawString(_font, new Vector2(textX, TabsHeight * 0.5f + _fontSize * 0.35f), tab.Label, HorizontalAlignment.Left, -1, _fontSize, tab.Active ? TextColor : MutedColor);
        }
        DrawLine(new Vector2(0, TabsHeight), new Vector2(Size.X, TabsHeight), BorderColor);
    }

    private void DrawRuler()
    {
        float top = RulerTop;
        DrawRect(new Rect2(HeaderWidth, top, Size.X - HeaderWidth, RulerHeight + BeatsHeight), HeaderColor);
        float textY = top + RulerHeight * 0.5f + _fontSize * 0.35f;
        for (int i = 0; i < _ticks.Count; i++)
        {
            TickGeom tick = _ticks[i];
            DrawLine(new Vector2(tick.X, top + RulerHeight - 6f * _scale), new Vector2(tick.X, top + RulerHeight), MutedColor);
            DrawString(_font, new Vector2(tick.X + 3f * _scale, textY), tick.Label, HorizontalAlignment.Left, -1, _fontSize, TextColor);
        }
        if (BeatsHeight > 0)
        {
            float beatsTop = top + RulerHeight;
            for (int i = 0; i < _beats.Count; i++)
            {
                BeatGeom beat = _beats[i];
                DrawLine(new Vector2(beat.X, beatsTop + (beat.Bar ? 0 : BeatsHeight * 0.5f)), new Vector2(beat.X, beatsTop + BeatsHeight), beat.Bar ? BarColor : BeatColor);
                if (beat.BarLabel is not null)
                    DrawString(_font, new Vector2(beat.X + 2f * _scale, beatsTop + BeatsHeight - 2f * _scale), beat.BarLabel, HorizontalAlignment.Left, -1, _fontSize - 2, MutedColor);
            }
        }
        DrawLine(new Vector2(HeaderWidth, top + RulerHeight + BeatsHeight), new Vector2(Size.X, top + RulerHeight + BeatsHeight), BorderColor);
        DrawString(_font, new Vector2(8f * _scale, textY), _sheetTitle, HorizontalAlignment.Left, HeaderWidth - 12f * _scale, _fontSize, MutedColor);
        if (_state.SnapToGrid)
            DrawString(_font, new Vector2(HeaderWidth - 40f * _scale, textY), "snap", HorizontalAlignment.Left, -1, _fontSize - 2, AccentColor);
    }

    private void DrawMarkerLane()
    {
        float top = MarkerLaneTop;
        DrawRect(new Rect2(HeaderWidth, top, Size.X - HeaderWidth, MarkerLaneHeight), PanelColor);
        DrawString(_font, new Vector2(8f * _scale, top + MarkerLaneHeight * 0.5f + _fontSize * 0.35f), "Logic", HorizontalAlignment.Left, -1, _fontSize, MutedColor);
        float textY = top + MarkerLaneHeight * 0.5f + _fontSize * 0.35f;
        for (int i = 0; i < _markers.Count; i++)
        {
            MarkerGeom m = _markers[i];
            Rect2 r = m.Rect;
            if (m.Region)
            {
                // Bracket: top bar with end caps spanning the region; the region also tints the track area.
                var tint = Alpha(m.Color, 0.08f);
                DrawRect(new Rect2(r.Position.X, TracksTop, r.Size.X, Math.Max(0, Size.Y - TracksTop)), tint);
                DrawRect(new Rect2(r.Position.X, top + 2f * _scale, r.Size.X, 3f * _scale), m.Color);
                DrawRect(new Rect2(r.Position.X, top + 2f * _scale, 2f * _scale, r.Size.Y - 4f * _scale), m.Color);
                DrawRect(new Rect2(r.End.X - 2f * _scale, top + 2f * _scale, 2f * _scale, r.Size.Y - 4f * _scale), m.Color);
                DrawString(_font, new Vector2(r.Position.X + 5f * _scale, textY + 2f * _scale), m.Label, HorizontalAlignment.Left, r.Size.X - 8f * _scale, _fontSize - 1, m.Color);
            }
            else
            {
                // Flag: line down through the tracks plus a label box.
                DrawLine(new Vector2(r.Position.X, top), new Vector2(r.Position.X, Size.Y), Alpha(m.Color, 0.5f));
                float labelWidth = _font.GetStringSize(m.Label, HorizontalAlignment.Left, -1, _fontSize - 1).X + 10f * _scale;
                DrawRect(new Rect2(r.Position.X, top + 3f * _scale, labelWidth, r.Size.Y - 6f * _scale), m.Color);
                DrawString(_font, new Vector2(r.Position.X + 5f * _scale, textY + 1f * _scale), m.Label, HorizontalAlignment.Left, -1, _fontSize - 1, Background);
            }
            if (m.Selected) DrawRect(new Rect2(r.Position.X - 1, top + 1, (m.Region ? r.Size.X : 10f * _scale) + 2, MarkerLaneHeight - 2), SelectionColor, false, 1.5f);
        }
        DrawLine(new Vector2(HeaderWidth, top + MarkerLaneHeight), new Vector2(Size.X, top + MarkerLaneHeight), BorderColor);
    }

    private void DrawTempos()
    {
        for (int i = 0; i < _tempos.Count; i++)
        {
            TempoGeom t = _tempos[i];
            DrawRect(t.Rect, BarColor);
            DrawRect(new Rect2(t.Rect.Position.X - 16f * _scale, t.Rect.Position.Y, 2f * _scale, t.Rect.Size.Y), AccentColor);
            DrawString(_font, new Vector2(t.Rect.Position.X + 4f * _scale, t.Rect.Position.Y + t.Rect.Size.Y - 3f * _scale), t.Label, HorizontalAlignment.Left, -1, _fontSize - 2, TextColor);
        }
    }

    private void DrawTracks()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            RowGeom row = _rows[i];
            if (row.Rect.End.Y < TracksTop || row.Rect.Position.Y > Size.Y) continue;
            DrawRect(new Rect2(HeaderWidth, row.Rect.Position.Y, Size.X - HeaderWidth, row.Rect.Size.Y), i % 2 == 0 ? Background : Alpha(PanelColor, 0.6f));
            DrawLine(new Vector2(HeaderWidth, row.Rect.End.Y), new Vector2(Size.X, row.Rect.End.Y), BorderColor);
        }
        // Vertical grid from ruler ticks.
        float gridTop = TracksTop;
        for (int i = 0; i < _ticks.Count; i++) DrawLine(new Vector2(_ticks[i].X, gridTop), new Vector2(_ticks[i].X, Size.Y), GridColor);
        for (int i = 0; i < _beats.Count; i++) if (_beats[i].Bar) DrawLine(new Vector2(_beats[i].X, gridTop), new Vector2(_beats[i].X, Size.Y), BeatColor);
    }

    private void DrawBlocks()
    {
        for (int i = 0; i < _blocks.Count; i++)
        {
            BlockGeom b = _blocks[i];
            Rect2 r = b.Rect;
            if (r.End.X < HeaderWidth || r.Position.X > Size.X || r.End.Y < TracksTop) continue;
            Color fill = b.Selected ? b.Color.Lightened(0.15f) : b.Color;
            DrawRect(r, Alpha(fill, 0.85f));
            DrawRect(new Rect2(r.Position, new Vector2(r.Size.X, 3f * _scale)), b.Color.Lightened(0.3f));
            if (b.Natural) DrawRect(new Rect2(r.End.X - 3f * _scale, r.Position.Y, 3f * _scale, r.Size.Y), Alpha(Background, 0.5f));
            if (r.Size.X > 24f * _scale)
            {
                float textX = r.Position.X + 5f * _scale;
                float clip = r.Size.X - 8f * _scale;
                DrawString(_font, new Vector2(textX, r.Position.Y + 4f * _scale + _fontSize), b.Label, HorizontalAlignment.Left, clip, _fontSize, Colors.White);
                DrawString(_font, new Vector2(textX, r.End.Y - 6f * _scale), b.Sub, HorizontalAlignment.Left, clip, _fontSize - 1, Alpha(Colors.White, 0.7f));
                // Waveform placeholder: a centre line band.
                float mid = r.Position.Y + r.Size.Y * 0.55f;
                DrawLine(new Vector2(r.Position.X + 2, mid), new Vector2(r.End.X - 2, mid), Alpha(Colors.White, 0.25f));
            }
            if (b.Selected) DrawRect(r.Grow(1), SelectionColor, false, 2f);
            else DrawRect(r, Alpha(Background, 0.6f), false, 1f);
        }
    }

    private void DrawHeaderColumn()
    {
        DrawRect(new Rect2(0, TracksTop, HeaderWidth, Math.Max(0, Size.Y - TracksTop)), HeaderColor);
        for (int i = 0; i < _rows.Count; i++)
        {
            RowGeom row = _rows[i];
            if (row.Rect.End.Y < TracksTop || row.Rect.Position.Y > Size.Y) continue;
            if (row.Selected) DrawRect(new Rect2(0, row.Rect.Position.Y, HeaderWidth, row.Rect.Size.Y), Alpha(AccentColor, 0.18f));
            DrawString(_font, new Vector2(8f * _scale, row.Rect.Position.Y + 6f * _scale + _fontSize), row.Label, HorizontalAlignment.Left, HeaderWidth - 16f * _scale, _fontSize, TextColor);
            DrawString(_font, new Vector2(8f * _scale, row.Rect.End.Y - 8f * _scale), row.Volume, HorizontalAlignment.Left, -1, _fontSize - 1, MutedColor);
            DrawLine(new Vector2(0, row.Rect.End.Y), new Vector2(HeaderWidth, row.Rect.End.Y), BorderColor);
        }
        DrawLine(new Vector2(HeaderWidth, TabsHeight), new Vector2(HeaderWidth, Size.Y), BorderColor);
        DrawRect(new Rect2(0, TabsHeight, HeaderWidth, RulerHeight + BeatsHeight), HeaderColor);
        if (_state.Sheet.IsNone) DrawRect(new Rect2(0, MarkerLaneTop, HeaderWidth, MarkerLaneHeight), HeaderColor);
    }

    private void DrawPlayhead()
    {
        double u = _state.Sheet.IsNone ? _playhead : _sheetValue;
        if (_state.Sheet.IsNone ? u < 0 : double.IsNaN(u)) return;
        float x = XOf(u);
        if (x < HeaderWidth || x > Size.X) return;
        DrawLine(new Vector2(x, RulerTop), new Vector2(x, Size.Y), PlayheadColor, 2f);
        // Head triangle in the ruler.
        float s = 6f * _scale;
        DrawColoredPolygon(_playheadPolygon.Fill(x, RulerTop, s), PlayheadColor);
    }

    private readonly PlayheadPolygon _playheadPolygon = new();

    private sealed class PlayheadPolygon
    {
        private readonly Vector2[] _points = new Vector2[3];
        public Vector2[] Fill(float x, float top, float s)
        {
            _points[0] = new Vector2(x - s, top);
            _points[1] = new Vector2(x + s, top);
            _points[2] = new Vector2(x, top + s * 1.4f);
            return _points;
        }
    }

    private void DrawCursor()
    {
        float x = XOf(_state.Cursor);
        if (x < HeaderWidth || x > Size.X) return;
        DrawLine(new Vector2(x, RulerTop + RulerHeight * 0.5f), new Vector2(x, Size.Y), Alpha(CursorColor, 0.6f), 1f);
    }

    // ---- playback following --------------------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (_state.Event is null) { if (_playhead >= 0 || !double.IsNaN(_sheetValue)) { _playhead = -1; _sheetValue = double.NaN; QueueRedraw(); } return; }
        EventInstance? instance = RunningInstance();
        double playhead = instance is null ? -1 : instance.TimelinePosition;
        double sheetValue = double.NaN;
        if (!_state.Sheet.IsNone)
        {
            ParameterDefinition? parameter = SheetParameter;
            if (parameter is not null)
            {
                if (instance is not null) sheetValue = instance.GetParameter(parameter.Name);
                else if (_state.IsGlobalParameter(parameter.Id) && _audition.System is { } system) sheetValue = system.GetGlobalParameter(parameter.Name);
            }
        }
        if (playhead != _playhead || !(double.IsNaN(sheetValue) && double.IsNaN(_sheetValue)) && sheetValue != _sheetValue)
        {
            _playhead = playhead;
            _sheetValue = sheetValue;
            QueueRedraw();
        }
    }

    /// <summary>First non-stopped audition instance of the edited event.</summary>
    public EventInstance? RunningInstance()
    {
        IReadOnlyList<EventInstance> instances = _audition.Instances;
        DirgeId id = _state.EventId;
        for (int i = 0; i < instances.Count; i++)
        {
            EventInstance instance = instances[i];
            if (instance.Description.Id == id && instance.State != PlaybackState.Stopped) return instance;
        }
        return null;
    }

    // ---- geometry records ----------------------------------------------------------------------------------------------

    private readonly record struct TabGeom(Rect2 Rect, string Label, DirgeId Parameter, bool Global, bool Active);
    private readonly record struct RowGeom(Rect2 Rect, Track Track, string Label, string Volume, bool Selected);
    private readonly record struct BlockGeom(Rect2 Rect, Instrument Instrument, Track Track, string Label, string Sub, Color Color, bool Selected, bool Natural);
    private readonly record struct MarkerGeom(Rect2 Rect, LogicMarker Marker, string Label, Color Color, bool Region, bool Selected);
    private readonly record struct TempoGeom(Rect2 Rect, TempoMarker Marker, string Label);
    private readonly record struct TickGeom(float X, string Label);
    private readonly record struct BeatGeom(float X, bool Bar, string? BarLabel);
}
