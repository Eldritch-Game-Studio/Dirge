#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Dirge.Profiling;
using Dirge.Tool.Core;
using Godot;

namespace Dirge.Tool.Profiler;

/// <summary>
/// One instance handle's lifetime inside a recording: <c>Start</c> → the tick it reached <c>Stopped</c>
/// (<c>RecordedTick.Ended</c>), or the recording's end when it was still playing.
/// </summary>
public struct LifetimeBar
{
    public int Handle;
    public string Event;
    public double Start;
    public double End;
    /// <summary>Still playing when the recording ended; drawn with an open right edge.</summary>
    public bool OpenEnd;
    /// <summary>Time of the <c>Stop</c>/<c>StopAll</c> API call that began its fade-out, NaN when it ended on its own or immediately.</summary>
    public double StopAt;
    public int Row;
}

/// <summary>
/// Draws a loaded <see cref="Recording"/> as event lifetime bars on a zoomable time axis. Wheel zooms around the cursor,
/// Shift+wheel scrolls rows, dragging empty space pans. Clicking a bar raises <see cref="BarClicked"/>.
/// </summary>
public sealed partial class RecordingCanvas : Control
{
    // Authored pixels at tool scale 1; the properties below read the live tool scale.
    private const float AxisHeightBase = 20f;
    private const float StripHeightBase = 18f;
    private const float RowHeightBase = 20f;
    private const float RowGapBase = 2f;
    private const float GutterBase = 40f;
    private const float FontSizeBase = 11f;
    private static float AxisHeight => AxisHeightBase * ToolScale.Current;
    private static float StripHeight => StripHeightBase * ToolScale.Current;
    private static float RowHeight => RowHeightBase * ToolScale.Current;
    private static float RowGap => RowGapBase * ToolScale.Current;
    private static float Gutter => GutterBase * ToolScale.Current;
    private const double MinPixelsPerSecond = 4.0;
    private const double MaxPixelsPerSecond = 4000.0;

    private readonly List<LifetimeBar> _bars = new();
    private readonly Dictionary<int, int> _open = new();
    private readonly Dictionary<int, string> _pendingEvents = new();
    private double _length;
    private double _pixelsPerSecond = 80.0;
    private double _scrollTime;
    private float _scrollY;
    private int _rowCount;
    private int _selectedHandle = -1;
    private double _divergenceTime = double.NaN;
    private bool _dragging;
    private bool _dragMoved;
    private Vector2 _dragOrigin;
    private double _dragScrollTime;
    private float _dragScrollY;
    private string _title = "No recording";

    /// <summary>Raised with the event path and handle of the clicked bar.</summary>
    public event Action<string, int>? BarClicked;

    public RecordingCanvas()
    {
        ToolDocuments.SetAuthoredMinimumSize(this, new Vector2(240, 80));
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
        FocusMode = FocusModeEnum.Click;
    }

    public IReadOnlyList<LifetimeBar> Bars => _bars;
    public double Length => _length;
    public int SelectedHandle => _selectedHandle;
    /// <summary>Label size in pixels at the current tool scale.</summary>
    public int FontSize => ToolScale.Px(FontSizeBase);

    public override void _EnterTree()
    {
        ToolScale.Changed += QueueRedraw;
        ToolTheme.Changed += QueueRedraw;
    }

    public override void _ExitTree()
    {
        ToolScale.Changed -= QueueRedraw;
        ToolTheme.Changed -= QueueRedraw;
    }

    /// <summary>Replaces the shown recording (null clears) and fits the whole length into the view.</summary>
    public void SetRecording(Recording? recording, string title)
    {
        _bars.Clear();
        _open.Clear();
        _pendingEvents.Clear();
        _selectedHandle = -1;
        _divergenceTime = double.NaN;
        _scrollY = 0f;
        _title = title;
        _length = 0;
        _rowCount = 0;
        if (recording is not null)
        {
            Build(recording);
        }
        FitAll();
        QueueRedraw();
    }

    /// <summary>Marks the tick where a replay diverged (NaN clears the marker).</summary>
    public void SetDivergence(double time)
    {
        _divergenceTime = time;
        QueueRedraw();
    }

    public void FitAll()
    {
        float width = MathF.Max(1f, Size.X - Gutter - 8f);
        _pixelsPerSecond = _length > 0 ? Math.Clamp(width / _length, MinPixelsPerSecond, MaxPixelsPerSecond) : 80.0;
        _scrollTime = 0;
        QueueRedraw();
    }

    private void Build(Recording recording)
    {
        List<RecordedTick> ticks = recording.Ticks;
        double end = 0;
        for (int t = 0; t < ticks.Count; t++)
        {
            RecordedTick tick = ticks[t];
            double time = tick.Time;
            end = tick.Time + tick.Dt;
            for (int c = 0; c < tick.Calls.Count; c++)
            {
                ApiCall call = tick.Calls[c];
                switch (call.Method)
                {
                    case "CreateInstance":
                        _pendingEvents[call.Result] = call.Name ?? "?";
                        break;
                    case "Start":
                        if (_open.TryGetValue(call.Handle, out int restarted))
                        {
                            Close(restarted, time);
                        }
                        _bars.Add(new LifetimeBar
                        {
                            Handle = call.Handle,
                            Event = _pendingEvents.TryGetValue(call.Handle, out string? path) ? path : "#" + call.Handle.ToString(CultureInfo.InvariantCulture),
                            Start = time,
                            End = time,
                            OpenEnd = true,
                            StopAt = double.NaN,
                        });
                        _open[call.Handle] = _bars.Count - 1;
                        break;
                    case "Stop":
                        if (call.Mode == (int)Dirge.Runtime.StopMode.AllowFadeout && _open.TryGetValue(call.Handle, out int fading)) MarkStop(fading, time);
                        break;
                    case "StopAll":
                        if (call.Mode == (int)Dirge.Runtime.StopMode.AllowFadeout)
                            foreach (KeyValuePair<int, int> pair in _open) MarkStop(pair.Value, time);
                        break;
                }
            }
            for (int e = 0; e < tick.Ended.Count; e++)
            {
                if (_open.TryGetValue(tick.Ended[e], out int index))
                {
                    Close(index, end);
                    _open.Remove(tick.Ended[e]);
                }
            }
        }
        _length = end;
        foreach (KeyValuePair<int, int> pair in _open)
        {
            LifetimeBar bar = _bars[pair.Value];
            bar.End = end;
            bar.OpenEnd = true;
            _bars[pair.Value] = bar;
        }
        _open.Clear();

        // One row per handle, ordered by first start.
        _bars.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Handle.CompareTo(b.Handle));
        var rows = new Dictionary<int, int>();
        for (int i = 0; i < _bars.Count; i++)
        {
            LifetimeBar bar = _bars[i];
            if (!rows.TryGetValue(bar.Handle, out int row))
            {
                row = rows.Count;
                rows[bar.Handle] = row;
            }
            bar.Row = row;
            _bars[i] = bar;
        }
        _rowCount = rows.Count;
    }

    private void Close(int index, double time)
    {
        LifetimeBar bar = _bars[index];
        bar.End = time;
        bar.OpenEnd = false;
        _bars[index] = bar;
    }

    private void MarkStop(int index, double time)
    {
        LifetimeBar bar = _bars[index];
        if (double.IsNaN(bar.StopAt)) bar.StopAt = time;
        _bars[index] = bar;
    }

    private float TimeToX(double time) => Gutter + (float)((time - _scrollTime) * _pixelsPerSecond);
    private double XToTime(float x) => _scrollTime + (x - Gutter) / _pixelsPerSecond;
    private float RowToY(int row) => AxisHeight + 4f + row * (RowHeight + RowGap) - _scrollY;

    public override void _GuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton button when button.ButtonIndex == MouseButton.WheelUp || button.ButtonIndex == MouseButton.WheelDown:
                if (!button.Pressed) return;
                if (button.ShiftPressed)
                {
                    float maxScroll = MathF.Max(0f, _rowCount * (RowHeight + RowGap) + AxisHeight + 8f - Size.Y);
                    _scrollY = Mathf.Clamp(_scrollY + (button.ButtonIndex == MouseButton.WheelDown ? RowHeight : -RowHeight), 0f, maxScroll);
                }
                else
                {
                    double anchor = XToTime(button.Position.X);
                    double factor = button.ButtonIndex == MouseButton.WheelUp ? 1.25 : 0.8;
                    _pixelsPerSecond = Math.Clamp(_pixelsPerSecond * factor, MinPixelsPerSecond, MaxPixelsPerSecond);
                    _scrollTime = anchor - (button.Position.X - Gutter) / _pixelsPerSecond;
                    ClampScroll();
                }
                AcceptEvent();
                QueueRedraw();
                break;
            case InputEventMouseButton button when button.ButtonIndex == MouseButton.Left || button.ButtonIndex == MouseButton.Middle:
                if (button.Pressed)
                {
                    GrabFocus();
                    _dragging = true;
                    _dragMoved = false;
                    _dragOrigin = button.Position;
                    _dragScrollTime = _scrollTime;
                    _dragScrollY = _scrollY;
                }
                else if (_dragging)
                {
                    _dragging = false;
                    if (!_dragMoved && button.ButtonIndex == MouseButton.Left) Click(button.Position);
                }
                AcceptEvent();
                break;
            case InputEventMouseMotion motion when _dragging:
                Vector2 delta = motion.Position - _dragOrigin;
                if (delta.LengthSquared() > 9f) _dragMoved = true;
                if (_dragMoved)
                {
                    _scrollTime = _dragScrollTime - delta.X / _pixelsPerSecond;
                    float maxScroll = MathF.Max(0f, _rowCount * (RowHeight + RowGap) + AxisHeight + 8f - Size.Y);
                    _scrollY = Mathf.Clamp(_dragScrollY - delta.Y, 0f, maxScroll);
                    ClampScroll();
                    QueueRedraw();
                }
                AcceptEvent();
                break;
        }
    }

    private void ClampScroll()
    {
        double visible = (Size.X - Gutter) / _pixelsPerSecond;
        double max = Math.Max(0, _length - visible * 0.5);
        _scrollTime = Math.Clamp(_scrollTime, -visible * 0.1, max);
    }

    private void Click(Vector2 position)
    {
        int hit = HitTest(position);
        if (hit < 0)
        {
            return;
        }
        LifetimeBar bar = _bars[hit];
        _selectedHandle = bar.Handle;
        QueueRedraw();
        BarClicked?.Invoke(bar.Event, bar.Handle);
    }

    /// <summary>Index into <see cref="Bars"/> under <paramref name="position"/>, or −1.</summary>
    public int HitTest(Vector2 position)
    {
        if (position.Y < AxisHeight) return -1;
        for (int i = 0; i < _bars.Count; i++)
        {
            LifetimeBar bar = _bars[i];
            float y = RowToY(bar.Row);
            float x0 = TimeToX(bar.Start);
            float x1 = MathF.Max(TimeToX(bar.End), x0 + 3f);
            if (position.Y >= y && position.Y < y + RowHeight && position.X >= x0 && position.X <= x1) return i;
        }
        return -1;
    }

    /// <summary>Selects a handle programmatically (−1 clears).</summary>
    public void SelectHandle(int handle)
    {
        _selectedHandle = handle;
        QueueRedraw();
    }

    public override void _Draw()
    {
        Vector2 size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), ProfilerPalette.Background);
        Font font = GetThemeDefaultFont();
        int fontSize = FontSize;

        if (_bars.Count == 0)
        {
            DrawString(font, new Vector2(8f, fontSize + 4f), _title, HorizontalAlignment.Left, -1, fontSize, ProfilerPalette.Muted);
            DrawString(font, new Vector2(8f, fontSize * 2f + 10f), "Record or load a recording to see event lifetimes.", HorizontalAlignment.Left, -1, fontSize, ProfilerPalette.Muted);
            return;
        }

        // Bars.
        for (int i = 0; i < _bars.Count; i++)
        {
            LifetimeBar bar = _bars[i];
            float y = RowToY(bar.Row);
            if (y + RowHeight < AxisHeight || y > size.Y) continue;
            float x0 = TimeToX(bar.Start);
            float x1 = TimeToX(bar.End);
            if (x1 < x0 + 3f) x1 = x0 + 3f;
            if (x1 < Gutter || x0 > size.X) continue;
            float left = MathF.Max(x0, Gutter);
            float right = MathF.Min(x1, size.X);
            DrawRect(new Rect2(left, y, right - left, RowHeight), ProfilerPalette.BarPlaying);
            if (!double.IsNaN(bar.StopAt))
            {
                // Fade-out tail: from the Stop call to the tick the instance actually finished.
                float stopX = Mathf.Clamp(TimeToX(bar.StopAt), left, right);
                DrawRect(new Rect2(stopX, y, right - stopX, RowHeight), ProfilerPalette.BarStopping);
            }
            if (bar.Handle == _selectedHandle)
            {
                DrawRect(new Rect2(left, y, right - left, RowHeight), ProfilerPalette.Selection, false, 2f);
            }
            if (bar.OpenEnd && x1 <= size.X)
            {
                // Open right edge: a dashed stub.
                for (int d = 0; d < 3; d++)
                {
                    DrawRect(new Rect2(x1 + 2f + d * 5f, y + RowHeight * 0.4f, 3f, RowHeight * 0.2f), ProfilerPalette.Muted);
                }
            }
            float textWidth = right - left - 6f;
            if (textWidth > 24f)
            {
                DrawString(font, new Vector2(left + 4f, y + RowHeight * 0.5f + fontSize * 0.35f), bar.Event, HorizontalAlignment.Left, textWidth, fontSize, ProfilerPalette.Text);
            }
        }

        // Row gutter (handles).
        DrawRect(new Rect2(0f, AxisHeight, Gutter, size.Y - AxisHeight), ProfilerPalette.Panel);
        for (int row = 0; row < _rowCount; row++)
        {
            float y = RowToY(row);
            if (y + RowHeight < AxisHeight || y > size.Y) continue;
            int handle = -1;
            for (int i = 0; i < _bars.Count; i++) if (_bars[i].Row == row) { handle = _bars[i].Handle; break; }
            DrawString(font, new Vector2(4f, y + RowHeight * 0.5f + fontSize * 0.35f), "#" + handle.ToString(CultureInfo.InvariantCulture), HorizontalAlignment.Left, Gutter - 6f, fontSize, ProfilerPalette.Muted);
        }

        // Time axis.
        DrawRect(new Rect2(0f, 0f, size.X, AxisHeight), ProfilerPalette.Panel);
        DrawLine(new Vector2(0f, AxisHeight), new Vector2(size.X, AxisHeight), ProfilerPalette.Border, 1f);
        double step = NiceStep(80.0 / _pixelsPerSecond);
        double first = Math.Floor(Math.Max(0, _scrollTime) / step) * step;
        for (double t = first; TimeToX(t) < size.X; t += step)
        {
            float x = TimeToX(t);
            if (x < Gutter) continue;
            DrawLine(new Vector2(x, AxisHeight - 6f), new Vector2(x, AxisHeight), ProfilerPalette.Muted, 1f);
            DrawLine(new Vector2(x, AxisHeight), new Vector2(x, size.Y), ProfilerPalette.Grid, 1f);
            DrawString(font, new Vector2(x + 3f, fontSize + 1f), FormatTime(t, step), HorizontalAlignment.Left, -1, fontSize, ProfilerPalette.Muted);
        }

        // Status strip: title left, interaction hint right.
        float stripY = size.Y - StripHeight;
        DrawRect(new Rect2(0f, stripY, size.X, StripHeight), ProfilerPalette.Panel);
        DrawLine(new Vector2(0f, stripY), new Vector2(size.X, stripY), ProfilerPalette.Border, 1f);
        DrawString(font, new Vector2(6f, stripY + fontSize + 2f), _title, HorizontalAlignment.Left, size.X * 0.6f, fontSize, ProfilerPalette.Text);
        DrawString(font, new Vector2(size.X * 0.6f + 6f, stripY + fontSize + 2f), "wheel zoom · shift+wheel rows · drag pan · click selects", HorizontalAlignment.Right, size.X * 0.4f - 12f, fontSize, ProfilerPalette.Muted);

        // End of recording.
        float endX = TimeToX(_length);
        if (endX >= Gutter && endX <= size.X)
        {
            DrawLine(new Vector2(endX, AxisHeight), new Vector2(endX, stripY), ProfilerPalette.Border, 2f);
        }
        if (!double.IsNaN(_divergenceTime))
        {
            float dx = TimeToX(_divergenceTime);
            if (dx >= Gutter && dx <= size.X)
            {
                DrawLine(new Vector2(dx, 0f), new Vector2(dx, stripY), ProfilerPalette.Playhead, 2f);
            }
        }
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double n = raw / magnitude;
        double nice = n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10;
        return nice * magnitude;
    }

    private static string FormatTime(double t, double step)
    {
        if (step >= 1) return t.ToString("0", CultureInfo.InvariantCulture) + " s";
        if (step >= 0.1) return t.ToString("0.0", CultureInfo.InvariantCulture);
        return t.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
