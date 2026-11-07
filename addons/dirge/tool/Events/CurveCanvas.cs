#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Dirge.Evaluation;
using Dirge.Project;
using Dirge.Runtime;
using Dirge.Tool.Core;
using Godot;
using Curve = Dirge.Project.Curve;

namespace Dirge.Tool.Events;

/// <summary>
/// Automation curve editor: X = driving parameter range, Y = target property range. Double-click adds a point,
/// drag moves (snapped), right-click picks the segment shape or deletes, Delete removes the selected point.
/// A vertical cursor shows the parameter's live value from audition (or the sheet cursor).
/// </summary>
public sealed partial class CurveCanvas : Control
{
    private const int MenuLinear = 1, MenuSmooth = 2, MenuStepped = 3, MenuDelete = 4;
    private const float PointRadiusBase = 5f;
    private const float PadLeftBase = 44f, PadBottomBase = 18f, PadTopBase = 8f, PadRightBase = 8f;

    private readonly EventEditorState _state;
    private readonly EventEdits _edits;
    private readonly IAudition _audition;
    private float _scale = 1f;
    private Font _font = null!;
    private int _fontSize = 12;
    private PopupMenu _menu = null!;

    private AutomationLane? _lane;
    private ParameterDefinition? _parameter;
    private float _yMin, _yMax = 1f;
    private string _yMinLabel = "", _yMaxLabel = "", _title = "";
    private int _layoutRevision = -1;

    private int _selected = -1;
    private int _dragging = -1;
    private int _menuPoint = -1;
    private int _menuSegment = -1;
    private object _dragToken = new();
    private readonly List<CurvePoint> _work = new();
    private readonly Vector2[] _segment = new Vector2[2];
    private double _cursorValue = double.NaN;

    private static readonly Color Background = new("1b1c20");
    private static readonly Color GridColor = new("2f3238");
    private static readonly Color AxisColor = new("565b66");
    private static readonly Color TextColor = new("8a8f9a");
    private static readonly Color LineColor = new("f08a3c");
    private static readonly Color PointColor = new("ffffff");
    private static readonly Color SelectedColor = new("9ad0ff");
    private static readonly Color CursorColor = new("ff4b4b");

    public CurveCanvas(EventEditorState state, EventEdits edits, IAudition audition)
    {
        _state = state;
        _edits = edits;
        _audition = audition;
        ClipContents = true;
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(240, 120);
    }

    public override void _Ready()
    {
        _font = GetThemeDefaultFont();
        _fontSize = Math.Clamp(GetThemeDefaultFontSize() - 3, 10, 14);
        _scale = Engine.IsEditorHint() && EditorInterface.Singleton is { } editor ? (float)editor.GetEditorScale() : 1f;
        _menu = new PopupMenu();
        _menu.IdPressed += OnMenu;
        AddChild(_menu);
        SetProcess(true);
    }

    public void Invalidate() => QueueRedraw();

    public bool HasSelectedPoint => _lane is not null && _selected >= 0 && _selected < _lane.Curve.Points.Count;

    public void DeleteSelectedPoint()
    {
        if (!HasSelectedPoint) return;
        _work.Clear();
        _work.AddRange(_lane!.Curve.Points);
        _work.RemoveAt(_selected);
        _edits.SetCurvePoints(null, "Delete curve point", _lane.Curve, _work);
        _selected = -1;
        QueueRedraw();
    }

    // ---- layout ------------------------------------------------------------------------------------------------------

    private float PadLeft => PadLeftBase * _scale;
    private float PadRight => PadRightBase * _scale;
    private float PadTop => PadTopBase * _scale;
    private float PadBottom => PadBottomBase * _scale;
    private float PlotWidth => Math.Max(1, Size.X - PadLeft - PadRight);
    private float PlotHeight => Math.Max(1, Size.Y - PadTop - PadBottom);

    private void EnsureLayout()
    {
        if (_layoutRevision == _state.Revision) return;
        _layoutRevision = _state.Revision;
        _lane = _state.FindLane(_state.SelectedLane, out Instrument? owner);
        _parameter = _lane is null ? null : _state.FindParameter(_lane.Parameter);
        if (_lane is null) { _title = ""; return; }
        _title = string.Concat(TargetLabel(_lane.Target, owner), " by ", _parameter?.Name ?? "(no parameter)");
        (_yMin, _yMax) = PropertyRange(_lane);
        _yMinLabel = _yMin.ToString("0.##", CultureInfo.InvariantCulture);
        _yMaxLabel = _yMax.ToString("0.##", CultureInfo.InvariantCulture);
        if (_selected >= _lane.Curve.Points.Count) _selected = -1;
    }

    public string TargetLabel(PropertyRef target, Instrument? owner) => target.Kind switch
    {
        PropertyKind.EventVolumeDb => "Event volume",
        PropertyKind.EventPitch => "Event pitch",
        PropertyKind.InstrumentVolumeDb => (owner?.Name ?? "Instrument") + " volume",
        PropertyKind.InstrumentPitch => (owner?.Name ?? "Instrument") + " pitch",
        PropertyKind.BusVolumeDb => (_state.FindBus(target.Owner)?.Name ?? "Bus") + " volume",
        PropertyKind.BusPitch => (_state.FindBus(target.Owner)?.Name ?? "Bus") + " pitch",
        PropertyKind.EffectParameter => (_state.FindBus(target.Owner)?.Name ?? "Bus") + " fx" + target.EffectIndex.ToString(CultureInfo.InvariantCulture) + "." + target.Name,
        PropertyKind.VcaVolumeDb => "VCA volume",
        _ => target.Kind.ToString(),
    };

    private static (float Min, float Max) PropertyRange(AutomationLane lane)
    {
        switch (lane.Target.Kind)
        {
            case PropertyKind.EventVolumeDb:
            case PropertyKind.InstrumentVolumeDb:
            case PropertyKind.BusVolumeDb:
            case PropertyKind.VcaVolumeDb:
                return (-60f, 12f);
            case PropertyKind.EventPitch:
            case PropertyKind.InstrumentPitch:
            case PropertyKind.BusPitch:
                return (0f, 4f);
            default:
            {
                float min = 0f, max = 1f;
                List<CurvePoint> points = lane.Curve.Points;
                for (int i = 0; i < points.Count; i++) { min = Math.Min(min, points[i].Y); max = Math.Max(max, points[i].Y); }
                if (max - min < 1e-3f) max = min + 1f;
                float pad = (max - min) * 0.1f;
                return (min - pad, max + pad);
            }
        }
    }

    private float XMin => _parameter?.Minimum ?? 0f;
    private float XMax => _parameter is null ? 1f : Math.Max(_parameter.Minimum + 1e-3f, _parameter.Maximum);

    private float ToScreenX(float x) => PadLeft + (x - XMin) / (XMax - XMin) * PlotWidth;
    private float ToScreenY(float y) => PadTop + (1f - (y - _yMin) / (_yMax - _yMin)) * PlotHeight;
    private float FromScreenX(float sx) => XMin + (sx - PadLeft) / PlotWidth * (XMax - XMin);
    private float FromScreenY(float sy) => _yMin + (1f - (sy - PadTop) / PlotHeight) * (_yMax - _yMin);

    private float SnapX(float x)
    {
        if (!_state.SnapToGrid) return Math.Clamp(x, XMin, XMax);
        float step = _parameter is { Kind: ParameterKind.Continuous } ? 0.1f * MathF.Pow(10, MathF.Floor(MathF.Log10(Math.Max(1e-3f, XMax - XMin)))) : 1f;
        return Math.Clamp(MathF.Round(x / step) * step, XMin, XMax);
    }

    private float SnapY(float y)
    {
        y = Math.Clamp(y, _yMin, _yMax);
        if (!_state.SnapToGrid) return y;
        float step = _yMax - _yMin > 20f ? 1f : 0.05f;
        return MathF.Round(y / step) * step;
    }

    // ---- drawing -----------------------------------------------------------------------------------------------------

    public override void _Draw()
    {
        EnsureLayout();
        DrawRect(new Rect2(Vector2.Zero, Size), Background);
        if (_lane is null)
        {
            DrawString(_font, new Vector2(12f * _scale, Size.Y * 0.5f), "Select an automation lane to edit its curve.", HorizontalAlignment.Left, -1, _fontSize, TextColor);
            return;
        }
        var plot = new Rect2(PadLeft, PadTop, PlotWidth, PlotHeight);
        // Grid: 4 horizontal, 10 vertical divisions.
        for (int i = 0; i <= 4; i++)
        {
            float y = PadTop + PlotHeight * i / 4f;
            DrawLine(new Vector2(PadLeft, y), new Vector2(PadLeft + PlotWidth, y), GridColor);
        }
        for (int i = 0; i <= 10; i++)
        {
            float x = PadLeft + PlotWidth * i / 10f;
            DrawLine(new Vector2(x, PadTop), new Vector2(x, PadTop + PlotHeight), GridColor);
        }
        DrawRect(plot, AxisColor, false, 1f);
        DrawString(_font, new Vector2(2f * _scale, PadTop + _fontSize), _yMaxLabel, HorizontalAlignment.Left, PadLeft - 4, _fontSize - 2, TextColor);
        DrawString(_font, new Vector2(2f * _scale, PadTop + PlotHeight), _yMinLabel, HorizontalAlignment.Left, PadLeft - 4, _fontSize - 2, TextColor);
        DrawString(_font, new Vector2(PadLeft, Size.Y - 4f * _scale), _parameter is null ? "" : _parameter.Minimum.ToString("0.##", CultureInfo.InvariantCulture), HorizontalAlignment.Left, -1, _fontSize - 2, TextColor);
        DrawString(_font, new Vector2(PadLeft + PlotWidth - 40f * _scale, Size.Y - 4f * _scale), _parameter is null ? "" : _parameter.Maximum.ToString("0.##", CultureInfo.InvariantCulture), HorizontalAlignment.Right, 40f * _scale, _fontSize - 2, TextColor);
        DrawString(_font, new Vector2(PadLeft + 6f * _scale, PadTop + _fontSize + 2f * _scale), _title, HorizontalAlignment.Left, PlotWidth - 12f * _scale, _fontSize, LineColor);

        List<CurvePoint> points = _lane.Curve.Points;
        if (points.Count == 0)
        {
            DrawString(_font, new Vector2(PadLeft + 6f * _scale, PadTop + PlotHeight * 0.5f), "Double-click to add points.", HorizontalAlignment.Left, -1, _fontSize - 1, TextColor);
        }
        else
        {
            // Flat extension before the first and after the last point (clamped curve semantics).
            DrawLine(new Vector2(PadLeft, ToScreenY(points[0].Y)), new Vector2(ToScreenX(points[0].X), ToScreenY(points[0].Y)), LineColor, 1.5f);
            DrawLine(new Vector2(ToScreenX(points[^1].X), ToScreenY(points[^1].Y)), new Vector2(PadLeft + PlotWidth, ToScreenY(points[^1].Y)), LineColor, 1.5f);
            for (int i = 0; i + 1 < points.Count; i++) DrawSegment(points[i], points[i + 1]);
            float radius = PointRadiusBase * _scale;
            for (int i = 0; i < points.Count; i++)
            {
                var p = new Vector2(ToScreenX(points[i].X), ToScreenY(points[i].Y));
                DrawCircle(p, radius, i == _selected ? SelectedColor : PointColor);
                DrawCircle(p, radius - 2f, i == _selected ? SelectedColor : LineColor);
            }
        }
        if (!double.IsNaN(_cursorValue))
        {
            float x = ToScreenX((float)_cursorValue);
            if (x >= PadLeft && x <= PadLeft + PlotWidth)
            {
                DrawLine(new Vector2(x, PadTop), new Vector2(x, PadTop + PlotHeight), CursorColor, 1.5f);
                float value = CurveMath.Evaluate(_lane.Curve, (float)_cursorValue);
                DrawCircle(new Vector2(x, ToScreenY(value)), 4f * _scale, CursorColor);
            }
        }
    }

    private void DrawSegment(CurvePoint a, CurvePoint b)
    {
        float ax = ToScreenX(a.X), ay = ToScreenY(a.Y), bx = ToScreenX(b.X), by = ToScreenY(b.Y);
        switch (a.ShapeToNext)
        {
            case CurveShape.Stepped:
                DrawLine(new Vector2(ax, ay), new Vector2(bx, ay), LineColor, 1.5f);
                DrawLine(new Vector2(bx, ay), new Vector2(bx, by), LineColor, 1.5f);
                break;
            case CurveShape.Smooth:
            {
                const int steps = 16;
                float px = ax, py = ay;
                for (int i = 1; i <= steps; i++)
                {
                    float t = i / (float)steps;
                    float s = t * t * (3f - 2f * t);
                    float x = ax + (bx - ax) * t;
                    float y = ay + (by - ay) * s;
                    DrawLine(new Vector2(px, py), new Vector2(x, y), LineColor, 1.5f);
                    px = x; py = y;
                }
                break;
            }
            default:
                DrawLine(new Vector2(ax, ay), new Vector2(bx, by), LineColor, 1.5f);
                break;
        }
    }

    // ---- live cursor ---------------------------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        EnsureLayout();
        double value = double.NaN;
        if (_lane is not null && _parameter is not null)
        {
            EventInstance? instance = null;
            IReadOnlyList<EventInstance> instances = _audition.Instances;
            for (int i = 0; i < instances.Count; i++)
                if (instances[i].Description.Id == _state.EventId && instances[i].State != PlaybackState.Stopped) { instance = instances[i]; break; }
            if (instance is not null) value = instance.GetParameter(_parameter.Name);
            else if (_state.IsGlobalParameter(_parameter.Id) && _audition.System is { } system) value = system.GetGlobalParameter(_parameter.Name);
            else if (_state.Sheet == _parameter.Id) value = _state.Cursor;
        }
        if (!(double.IsNaN(value) && double.IsNaN(_cursorValue)) && value != _cursorValue)
        {
            _cursorValue = value;
            QueueRedraw();
        }
    }

    // ---- input ---------------------------------------------------------------------------------------------------------

    public override void _GuiInput(InputEvent @event)
    {
        EnsureLayout();
        if (_lane is null) return;
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                if (button.Pressed)
                {
                    GrabFocus();
                    int hit = PointAt(button.Position);
                    if (button.DoubleClick && hit < 0) { AddPoint(button.Position); }
                    else if (hit >= 0) { _selected = hit; _dragging = hit; _dragToken = new object(); }
                    else { _selected = -1; }
                    QueueRedraw();
                }
                else if (_dragging >= 0)
                {
                    _dragging = -1;
                    _edits.EndContinuous();
                }
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } right:
                OpenMenu(right.Position);
                AcceptEvent();
                break;
            case InputEventMouseMotion motion when _dragging >= 0:
                DragPoint(motion.Position);
                AcceptEvent();
                break;
        }
    }

    private int PointAt(Vector2 p)
    {
        List<CurvePoint> points = _lane!.Curve.Points;
        float r = PointRadiusBase * _scale + 3f;
        for (int i = points.Count - 1; i >= 0; i--)
        {
            var sp = new Vector2(ToScreenX(points[i].X), ToScreenY(points[i].Y));
            if (sp.DistanceTo(p) <= r) return i;
        }
        return -1;
    }

    private void AddPoint(Vector2 p)
    {
        float x = SnapX(FromScreenX(p.X));
        float y = SnapY(FromScreenY(p.Y));
        _work.Clear();
        _work.AddRange(_lane!.Curve.Points);
        CurveShape shape = CurveShape.Linear;
        for (int i = 0; i < _work.Count; i++) if (_work[i].X <= x) shape = _work[i].ShapeToNext;
        _work.Add(new CurvePoint(x, y, shape));
        _edits.SetCurvePoints(null, "Add curve point", _lane.Curve, _work);
        List<CurvePoint> points = _lane.Curve.Points;
        _selected = -1;
        for (int i = 0; i < points.Count; i++) if (points[i].X == x && points[i].Y == y) _selected = i;
    }

    private void DragPoint(Vector2 p)
    {
        List<CurvePoint> points = _lane!.Curve.Points;
        if (_dragging >= points.Count) { _dragging = -1; return; }
        float x = SnapX(FromScreenX(p.X));
        float y = SnapY(FromScreenY(p.Y));
        // Keep X between neighbours so the point order (and the selection index) is stable while dragging.
        if (_dragging > 0) x = Math.Max(x, points[_dragging - 1].X);
        if (_dragging + 1 < points.Count) x = Math.Min(x, points[_dragging + 1].X);
        CurvePoint current = points[_dragging];
        if (current.X == x && current.Y == y) return;
        _work.Clear();
        _work.AddRange(points);
        _work[_dragging] = new CurvePoint(x, y, current.ShapeToNext);
        _edits.SetCurvePoints(_dragToken, "Move curve point", _lane.Curve, _work);
        _selected = _dragging;
    }

    private void OpenMenu(Vector2 p)
    {
        List<CurvePoint> points = _lane!.Curve.Points;
        _menuPoint = PointAt(p);
        _menuSegment = -1;
        float x = FromScreenX(p.X);
        for (int i = 0; i + 1 < points.Count; i++) if (points[i].X <= x && x <= points[i + 1].X) _menuSegment = i;
        if (_menuPoint >= 0 && _menuPoint + 1 < points.Count) _menuSegment = _menuPoint;
        _menu.Clear();
        if (_menuSegment >= 0)
        {
            CurveShape shape = points[_menuSegment].ShapeToNext;
            _menu.AddRadioCheckItem("Linear", MenuLinear);
            _menu.SetItemChecked(_menu.GetItemIndex(MenuLinear), shape == CurveShape.Linear);
            _menu.AddRadioCheckItem("Smooth", MenuSmooth);
            _menu.SetItemChecked(_menu.GetItemIndex(MenuSmooth), shape == CurveShape.Smooth);
            _menu.AddRadioCheckItem("Stepped", MenuStepped);
            _menu.SetItemChecked(_menu.GetItemIndex(MenuStepped), shape == CurveShape.Stepped);
        }
        if (_menuPoint >= 0)
        {
            if (_menuSegment >= 0) _menu.AddSeparator();
            _menu.AddItem("Delete point", MenuDelete);
            _selected = _menuPoint;
        }
        if (_menu.ItemCount == 0) return;
        _menu.Position = (Vector2I)(GetScreenPosition() + p);
        _menu.Popup();
        QueueRedraw();
    }

    private void OnMenu(long id)
    {
        if (_lane is null) return;
        List<CurvePoint> points = _lane.Curve.Points;
        if (id == MenuDelete)
        {
            if (_menuPoint >= 0 && _menuPoint < points.Count) { _selected = _menuPoint; DeleteSelectedPoint(); }
            return;
        }
        if (_menuSegment < 0 || _menuSegment >= points.Count) return;
        CurveShape shape = id switch { MenuSmooth => CurveShape.Smooth, MenuStepped => CurveShape.Stepped, _ => CurveShape.Linear };
        _work.Clear();
        _work.AddRange(points);
        CurvePoint p = _work[_menuSegment];
        _work[_menuSegment] = new CurvePoint(p.X, p.Y, shape);
        _edits.SetCurvePoints(null, "Curve shape", _lane.Curve, _work);
        QueueRedraw();
    }
}
