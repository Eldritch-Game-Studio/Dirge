#nullable enable
using System;
using Dirge.Runtime;
using Dirge.Tool.Core;
using Godot;
using SysVector3 = System.Numerics.Vector3;

namespace Dirge.Tool.Transport;

/// <summary>
/// Top-down 3D preview: the listener sits at the center (forward arrow), the emitter is a draggable dot on the Y=0 plane,
/// rings mark the selected event's min/max distance. Screen up is −Z (Godot forward), screen right is +X.
/// The wheel rotates the listener. Writes go to <see cref="Emitter"/>/<see cref="Listener"/> and raise <see cref="Changed"/>.
/// </summary>
public sealed partial class PreviewPanner : Control
{
    // Authored pixels at tool scale 1.
    private const float HandleRadiusBase = 7f;
    private const float FontSizeBase = 11f;
    private const float WheelDegrees = 7.5f;
    private const float Margin = 0.88f;

    private Attributes3D _emitter = new(new SysVector3(3f, 0f, -4f), SysVector3.Zero, -SysVector3.UnitZ, SysVector3.UnitY);
    private Attributes3D _listener = Attributes3D.Origin;
    private float _minDistance = 1f, _maxDistance = 20f;
    private bool _dragging;
    private Font _font = ThemeDB.FallbackFont;
    private Color _bg = new(0.09f, 0.10f, 0.12f), _grid = new(0.18f, 0.20f, 0.24f), _fg = new(0.85f, 0.86f, 0.88f), _muted = new(0.5f, 0.53f, 0.58f), _accent = new(0.96f, 0.62f, 0.26f);
    private float _lastScale = 1f;
    private readonly Vector2[] _arrow = new Vector2[3];

    /// <summary>Raised after the emitter or listener changed through pointer input.</summary>
    public event Action? Changed;

    public Attributes3D Emitter
    {
        get => _emitter;
        set { if (!_emitter.Equals(value)) { _emitter = value; QueueRedraw(); } }
    }

    public Attributes3D Listener
    {
        get => _listener;
        set { if (!_listener.Equals(value)) { _listener = value; QueueRedraw(); } }
    }

    public PreviewReadout Readout => PreviewReadout.Compute(_emitter, _listener, _minDistance, _maxDistance);

    /// <summary>Readout text size in pixels at the current tool scale.</summary>
    public int FontSize => ToolScale.Px(FontSizeBase);
    private static float UiScale => ToolScale.Current;

    public PreviewPanner()
    {
        Name = "PreviewPanner";
        ToolDocuments.SetAuthoredMinimumSize(this, new Vector2(180, 150));
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.Click;
        ClipContents = true;
    }

    public void SetDistances(float min, float max)
    {
        if (_minDistance == min && _maxDistance == max) return;
        _minDistance = min; _maxDistance = max;
        QueueRedraw();
    }

    public void SetColors(Color bg, Color grid, Color fg, Color muted, Color accent)
    {
        _bg = bg; _grid = grid; _fg = fg; _muted = muted; _accent = accent;
        QueueRedraw();
    }

    public override void _EnterTree() => ToolScale.Changed += QueueRedraw;

    public override void _ExitTree() => ToolScale.Changed -= QueueRedraw;

    public override void _Notification(int what)
    {
        if (what == NotificationThemeChanged) _font = GetThemeDefaultFont() ?? ThemeDB.FallbackFont;
        if (what == NotificationResized) QueueRedraw();
    }

    public override void _GuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                if (button.Pressed)
                {
                    GrabFocus();
                    // Press anywhere places the emitter; the drag continues from there.
                    _dragging = true;
                    MoveEmitter(button.Position);
                }
                else
                {
                    _dragging = false;
                }
                AcceptEvent();
                break;
            case InputEventMouseMotion motion when _dragging && (motion.ButtonMask & MouseButtonMask.Left) != 0:
                MoveEmitter(motion.Position);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                RotateListener(WheelDegrees);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                RotateListener(-WheelDegrees);
                AcceptEvent();
                break;
        }
    }

    public void RotateListener(float degrees)
    {
        SysVector3 forward = PreviewReadout.RotateYaw(_listener.Forward, degrees);
        _listener = _listener with { Forward = forward };
        QueueRedraw();
        Changed?.Invoke();
    }

    private void MoveEmitter(Vector2 screen)
    {
        SysVector3 world = ToWorld(screen);
        _emitter = _emitter with { Position = world };
        QueueRedraw();
        Changed?.Invoke();
    }

    /// <summary>Pixels per metre so the max-distance ring (and the emitter) stay inside the canvas.</summary>
    private float PixelsPerMetre()
    {
        Vector2 size = Size;
        float half = MathF.Min(size.X, size.Y) * 0.5f * Margin;
        float emitterRadius = MathF.Sqrt(_emitter.Position.X * _emitter.Position.X + _emitter.Position.Z * _emitter.Position.Z);
        float extent = MathF.Max(MathF.Max(_maxDistance, emitterRadius), 1f);
        return half / extent;
    }

    private Vector2 ToScreen(SysVector3 world)
    {
        Vector2 center = Size * 0.5f;
        return new Vector2(center.X + world.X * _lastScale, center.Y + world.Z * _lastScale);
    }

    private SysVector3 ToWorld(Vector2 screen)
    {
        Vector2 center = Size * 0.5f;
        float scale = _lastScale <= 0f ? 1f : _lastScale;
        return new SysVector3((screen.X - center.X) / scale, 0f, (screen.Y - center.Y) / scale);
    }

    public override void _Draw()
    {
        Vector2 size = Size;
        Vector2 center = size * 0.5f;
        float ui = UiScale;
        _lastScale = PixelsPerMetre();
        DrawRect(new Rect2(Vector2.Zero, size), _bg);

        // Grid every metre (thinned when dense).
        float step = _lastScale;
        while (step < 14f * ui) step *= 2f;
        for (float gx = center.X % step; gx < size.X; gx += step) DrawLine(new Vector2(gx, 0), new Vector2(gx, size.Y), _grid);
        for (float gy = center.Y % step; gy < size.Y; gy += step) DrawLine(new Vector2(0, gy), new Vector2(size.X, gy), _grid);

        // Distance rings.
        DrawArc(center, _minDistance * _lastScale, 0, MathF.Tau, 64, _accent with { A = 0.6f }, 1f, true);
        DrawArc(center, _maxDistance * _lastScale, 0, MathF.Tau, 96, _muted, 1f, true);

        // Listener: dot + forward arrow.
        SysVector3 f = _listener.Forward;
        var forward = new Vector2(f.X, f.Z);
        if (forward.LengthSquared() < 1e-6f) forward = new Vector2(0, -1);
        forward = forward.Normalized();
        Vector2 tip = center + forward * (26f * ui);
        Vector2 right = new(-forward.Y, forward.X);
        DrawLine(center, tip, _fg, 2f * ui, true);
        _arrow[0] = tip + forward * (6f * ui); _arrow[1] = tip - right * (5f * ui); _arrow[2] = tip + right * (5f * ui);
        DrawColoredPolygon(_arrow, _fg);
        DrawCircle(center, 5f * ui, _fg);

        // Emitter dot and its forward.
        Vector2 emitter = ToScreen(_emitter.Position);
        SysVector3 ef = _emitter.Forward;
        var emitterForward = new Vector2(ef.X, ef.Z);
        if (emitterForward.LengthSquared() > 1e-6f) DrawLine(emitter, emitter + emitterForward.Normalized() * (12f * ui), _accent, 1f, true);
        DrawLine(center, emitter, _accent with { A = 0.35f }, 1f, true);
        float handle = HandleRadiusBase * ui;
        DrawCircle(emitter, handle, _accent);
        DrawCircle(emitter, handle, _bg, false, 1.5f, true);

        // Readouts.
        PreviewReadout r = Readout;
        int fontSize = FontSize;
        float line = fontSize + 4f * ui;
        float x = 6f * ui;
        float y = line;
        DrawString(_font, new Vector2(x, y), "dist " + r.Distance.ToString("F2") + " m   norm " + r.DistanceNormalized.ToString("F2"), HorizontalAlignment.Left, -1, fontSize, _fg); y += line;
        DrawString(_font, new Vector2(x, y), "dir " + r.Direction.ToString("F1") + "°   elev " + r.Elevation.ToString("F1") + "°", HorizontalAlignment.Left, -1, fontSize, _fg); y += line;
        DrawString(_font, new Vector2(x, y), "cone " + r.ConeAngle.ToString("F1") + "°   orient " + r.Orientation.ToString("F1") + "°", HorizontalAlignment.Left, -1, fontSize, _fg); y += line;
        DrawString(_font, new Vector2(x, y), "speed " + r.Speed.ToString("F2") + "   |v| " + r.SpeedAbsolute.ToString("F2"), HorizontalAlignment.Left, -1, fontSize, _muted);
        DrawString(_font, new Vector2(x, size.Y - x), "x " + _emitter.Position.X.ToString("F1") + "  z " + _emitter.Position.Z.ToString("F1") + "   min/max " + _minDistance.ToString("F1") + "/" + _maxDistance.ToString("F1"), HorizontalAlignment.Left, -1, fontSize, _muted);
    }
}
