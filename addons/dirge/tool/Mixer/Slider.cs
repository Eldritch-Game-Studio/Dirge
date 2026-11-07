#nullable enable
using System;
using Godot;
using NecronomiDom.Dom;
using NecronomiDom.Paint;

namespace Dirge.Tool.Mixer;

/// <summary>
/// A div-based fader/slider driven by DOM pointer events: press jumps to the pointer, drag moves relatively (Shift = fine),
/// wheel nudges by <see cref="WheelStep"/>, a quick second press resets to <see cref="ResetValue"/>. The thumb position is
/// written as an inline style so updates stay on the host's retained inline-style path.
/// </summary>
internal sealed class Slider
{
    private const double DoubleClickSeconds = 0.35;
    private const float FineScale = 0.1f;

    private readonly Dom _dom;
    private readonly HTMLElement _track;
    private readonly HTMLElement _thumb;
    private readonly HTMLElement? _fill;
    private readonly bool _vertical;
    private readonly int _lengthPx;
    private readonly int _thumbPx;
    private float _min, _max;
    private float _value;
    private int _lastThumbPx = int.MinValue;
    private bool _dragging;
    private int _pointerId;
    private double _dragOrigin;
    private float _dragStartValue;
    private double _lastPressTime = double.NegativeInfinity;

    public float WheelStep { get; set; } = 0.5f;
    public float ResetValue { get; set; }
    /// <summary>Snap increment applied to every produced value; 0 = continuous.</summary>
    public float Step { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Raised during interaction. <c>continuous</c> is true while a drag is in progress (coalesce the undo entry).</summary>
    public event Action<float, bool>? ValueChanged;
    /// <summary>Raised once when a drag ends (after the last <see cref="ValueChanged"/>).</summary>
    public event Action? DragEnded;
    /// <summary>Raised before the first value of an interaction (used to scope snapshot properties on touch).</summary>
    public event Action? Touched;

    public float Value => _value;
    public bool IsDragging => _dragging;
    public HTMLElement Track => _track;

    /// <param name="lengthPx">Travel length of the track's inner area along the slider axis (CSS px, must match the stylesheet).</param>
    /// <param name="thumbPx">Thumb size along the axis (CSS px).</param>
    public Slider(Dom dom, HTMLElement track, HTMLElement thumb, HTMLElement? fill, bool vertical, float min, float max, int lengthPx, int thumbPx)
    {
        _dom = dom; _track = track; _thumb = thumb; _fill = fill; _vertical = vertical; _min = min; _max = max; _lengthPx = lengthPx; _thumbPx = thumbPx;
        _dom.On(track, "pointerdown", OnPointerDown);
        _dom.On(track, "pointermove", OnPointerMove);
        _dom.On(track, "pointerup", OnPointerUp);
        _dom.On(track, "pointercancel", OnPointerUp);
        _dom.On(track, "wheel", OnWheel);
    }

    public void SetRange(float min, float max)
    {
        _min = min; _max = max;
        SetValue(_value);
    }

    /// <summary>Display a value without raising events.</summary>
    public void SetValue(float value)
    {
        _value = Math.Clamp(value, _min, _max);
        int travel = _lengthPx - _thumbPx;
        float norm = _max > _min ? (_value - _min) / (_max - _min) : 0f;
        int px = (int)MathF.Round(_vertical ? (1f - norm) * travel : norm * travel);
        if (px == _lastThumbPx) return;
        _lastThumbPx = px;
        _thumb.Style.SetProperty(_vertical ? "top" : "left", Dom.Px(px));
        _fill?.Style.SetProperty("width", Dom.Px(px + _thumbPx / 2));
    }

    private void OnPointerDown(Event e)
    {
        if (!Enabled || e is not PointerEvent p || p.Button != 0) return;
        e.PreventDefault();
        double now = Time.GetTicksMsec() / 1000.0;
        bool doubleClick = now - _lastPressTime < DoubleClickSeconds;
        _lastPressTime = now;
        if (doubleClick)
        {
            Touched?.Invoke();
            Emit(ResetValue, false);
            return;
        }

        _pointerId = p.PointerId;
        _dragging = true;
        _track.SetPointerCapture(p.PointerId);
        Touched?.Invoke();
        double pointer = _vertical ? p.ClientY : p.ClientX;
        // Pressing away from the thumb jumps there first; the drag then continues relative to this point.
        if (_dom.TryGetBounds(_track, out Rect bounds))
        {
            double inner = _vertical ? pointer - bounds.Y : pointer - bounds.X;
            double travel = _lengthPx - _thumbPx;
            double norm = Math.Clamp((inner - _thumbPx / 2.0) / travel, 0, 1);
            if (_vertical) norm = 1 - norm;
            float jumped = _min + (float)norm * (_max - _min);
            if (MathF.Abs(jumped - _value) > (_max - _min) * (_thumbPx / (float)_lengthPx)) Emit(jumped, true);
        }
        _dragOrigin = pointer;
        _dragStartValue = _value;
    }

    private void OnPointerMove(Event e)
    {
        if (!_dragging || e is not PointerEvent p) return;
        double pointer = _vertical ? p.ClientY : p.ClientX;
        double delta = pointer - _dragOrigin;
        if (_vertical) delta = -delta;
        float scale = Input.IsKeyPressed(Key.Shift) ? FineScale : 1f;
        float range = _max - _min;
        float next = _dragStartValue + (float)(delta / (_lengthPx - _thumbPx)) * range * scale;
        // Re-anchor so toggling Shift mid-drag does not jump.
        _dragOrigin = pointer;
        _dragStartValue = Math.Clamp(next, _min, _max);
        Emit(next, true);
    }

    private void OnPointerUp(Event e)
    {
        if (!_dragging) return;
        _dragging = false;
        if (_track.HasPointerCapture(_pointerId)) _track.ReleasePointerCapture(_pointerId);
        DragEnded?.Invoke();
    }

    private void OnWheel(Event e)
    {
        if (!Enabled || e is not WheelEvent w) return;
        e.PreventDefault();
        double delta = w.DeltaY != 0 ? w.DeltaY : w.DeltaX;
        if (delta == 0) return;
        float step = WheelStep * (Input.IsKeyPressed(Key.Shift) ? FineScale : 1f);
        Touched?.Invoke();
        Emit(_value + (delta < 0 ? step : -step), false);
        DragEnded?.Invoke();
    }

    private void Emit(float value, bool continuous)
    {
        value = Math.Clamp(value, _min, _max);
        if (Step > 0f) value = _min + MathF.Round((value - _min) / Step) * Step;
        SetValue(value);
        ValueChanged?.Invoke(_value, continuous);
    }
}
