#nullable enable
using System;
using System.Globalization;
using Godot;

namespace Dirge.Tool.Profiler;

/// <summary>Colors shared by the profiler's Godot canvases; mirrors the dark theme of the documents.</summary>
internal static class ProfilerPalette
{
    public static readonly Color Background = new("1a1b1e");
    public static readonly Color Panel = new("222327");
    public static readonly Color Grid = new("2f3137");
    public static readonly Color Border = new("3a3c44");
    public static readonly Color Text = new("d8d9dc");
    public static readonly Color Muted = new("8a8d96");
    public static readonly Color Accent = new("f2a93b");
    public static readonly Color Series2 = new("5bb8f5");
    public static readonly Color BarPlaying = new("3f8f5a");
    public static readonly Color BarStopping = new("8f6a3f");
    public static readonly Color BarVirtual = new("4a4d57");
    public static readonly Color Selection = new("ffffff");
    public static readonly Color Playhead = new("e05a5a");
}

/// <summary>
/// Rolling 10 s graph of model update time (ms) and real voice count. Samples go into fixed ring buffers
/// (<see cref="Push"/>); drawing walks the buffers and draws line segments directly, so steady state allocates nothing.
/// </summary>
public sealed partial class UpdateGraph : Control
{
    public const double WindowSeconds = 10.0;
    private const int Capacity = 4096;
    private const float LeftGutter = 44f;
    private const float RightGutter = 40f;
    private const float TopGutter = 18f;
    private const float BottomGutter = 16f;

    private readonly double[] _time = new double[Capacity];
    private readonly float[] _ms = new float[Capacity];
    private readonly int[] _voices = new int[Capacity];
    private int _head;
    private int _count;
    private double _now;

    private float _shownMs = float.NaN;
    private int _shownVoices = -1;
    private string _msLabel = "— ms";
    private string _voiceLabel = "— voices";
    private float _msScale = 1f;
    private int _voiceScale = 8;
    private string _msScaleLabel = "1 ms";
    private string _voiceScaleLabel = "8";
    private string _midMsLabel = "0.5";
    private string _midVoiceLabel = "4";

    /// <summary>Voice cap used as the minimum of the voice axis (the system's MaxRealVoices).</summary>
    public int VoiceCapacity { get; set; } = 64;

    public UpdateGraph()
    {
        CustomMinimumSize = new Vector2(240, 120);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
    }

    public int SampleCount => _count;

    /// <summary>Appends a sample stamped with <paramref name="wallSeconds"/> (monotonic; see <see cref="Advance"/>).</summary>
    public void Push(double wallSeconds, float updateMs, int realVoices)
    {
        int index = (_head + _count) % Capacity;
        if (_count == Capacity) _head = (_head + 1) % Capacity; else _count++;
        _time[index] = wallSeconds;
        _ms[index] = updateMs;
        _voices[index] = realVoices;
        _now = wallSeconds;
        if (_shownMs != updateMs)
        {
            _shownMs = updateMs;
            _msLabel = updateMs.ToString("0.00", CultureInfo.InvariantCulture) + " ms";
        }
        if (_shownVoices != realVoices)
        {
            _shownVoices = realVoices;
            _voiceLabel = realVoices.ToString(CultureInfo.InvariantCulture) + " voices";
        }
    }

    /// <summary>Moves the window's right edge to <paramref name="wallSeconds"/> without adding a sample and redraws.</summary>
    public void Advance(double wallSeconds)
    {
        _now = wallSeconds;
        QueueRedraw();
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
        _shownMs = float.NaN;
        _shownVoices = -1;
        _msLabel = "— ms";
        _voiceLabel = "— voices";
        QueueRedraw();
    }

    private void UpdateScales()
    {
        float maxMs = 0f;
        int maxVoices = 0;
        double oldest = _now - WindowSeconds;
        for (int i = 0; i < _count; i++)
        {
            int index = (_head + i) % Capacity;
            if (_time[index] < oldest) continue;
            if (_ms[index] > maxMs) maxMs = _ms[index];
            if (_voices[index] > maxVoices) maxVoices = _voices[index];
        }

        float msScale = NiceCeiling(MathF.Max(maxMs * 1.15f, 0.5f));
        if (msScale != _msScale)
        {
            _msScale = msScale;
            _msScaleLabel = msScale.ToString("0.##", CultureInfo.InvariantCulture) + " ms";
            _midMsLabel = (msScale * 0.5f).ToString("0.##", CultureInfo.InvariantCulture);
        }

        int voiceScale = Math.Max(VoiceCapacity, 8);
        if (maxVoices > voiceScale) voiceScale = (int)NiceCeiling(maxVoices);
        if (voiceScale != _voiceScale)
        {
            _voiceScale = voiceScale;
            _voiceScaleLabel = voiceScale.ToString(CultureInfo.InvariantCulture);
            _midVoiceLabel = (voiceScale / 2).ToString(CultureInfo.InvariantCulture);
        }
    }

    private static float NiceCeiling(float value)
    {
        if (value <= 0f) return 1f;
        float magnitude = MathF.Pow(10f, MathF.Floor(MathF.Log10(value)));
        float normalized = value / magnitude;
        float nice = normalized <= 1f ? 1f : normalized <= 2f ? 2f : normalized <= 5f ? 5f : 10f;
        return nice * magnitude;
    }

    public override void _Draw()
    {
        Vector2 size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), ProfilerPalette.Background);
        var plot = new Rect2(LeftGutter, TopGutter, MathF.Max(1f, size.X - LeftGutter - RightGutter), MathF.Max(1f, size.Y - TopGutter - BottomGutter));
        DrawRect(plot, ProfilerPalette.Panel);
        UpdateScales();

        Font font = GetThemeDefaultFont();
        int fontSize = Math.Max(10, GetThemeDefaultFontSize() - 3);

        // Horizontal grid: 0, 25, 50, 75, 100 %.
        for (int i = 0; i <= 4; i++)
        {
            float y = plot.Position.Y + plot.Size.Y * (1f - i / 4f);
            DrawLine(new Vector2(plot.Position.X, y), new Vector2(plot.End.X, y), ProfilerPalette.Grid, 1f);
        }
        // Vertical grid: one per second.
        for (int s = 0; s <= (int)WindowSeconds; s++)
        {
            double t = _now - WindowSeconds + s;
            double frac = Math.Floor(t);
            float x = plot.Position.X + (float)((frac - (_now - WindowSeconds)) / WindowSeconds) * plot.Size.X;
            if (x < plot.Position.X || x > plot.End.X) continue;
            DrawLine(new Vector2(x, plot.Position.Y), new Vector2(x, plot.End.Y), ProfilerPalette.Grid, 1f);
        }
        DrawRect(plot, ProfilerPalette.Border, false, 1f);

        // Axis labels: ms on the left, voices on the right.
        float textY = plot.Position.Y + fontSize * 0.5f;
        DrawString(font, new Vector2(2f, textY), _msScaleLabel, HorizontalAlignment.Left, LeftGutter - 4f, fontSize, ProfilerPalette.Accent);
        DrawString(font, new Vector2(2f, plot.Position.Y + plot.Size.Y * 0.5f + fontSize * 0.35f), _midMsLabel, HorizontalAlignment.Left, LeftGutter - 4f, fontSize, ProfilerPalette.Muted);
        DrawString(font, new Vector2(2f, plot.End.Y), "0", HorizontalAlignment.Left, LeftGutter - 4f, fontSize, ProfilerPalette.Muted);
        DrawString(font, new Vector2(plot.End.X + 4f, textY), _voiceScaleLabel, HorizontalAlignment.Left, RightGutter - 4f, fontSize, ProfilerPalette.Series2);
        DrawString(font, new Vector2(plot.End.X + 4f, plot.Position.Y + plot.Size.Y * 0.5f + fontSize * 0.35f), _midVoiceLabel, HorizontalAlignment.Left, RightGutter - 4f, fontSize, ProfilerPalette.Muted);
        DrawString(font, new Vector2(plot.End.X + 4f, plot.End.Y), "0", HorizontalAlignment.Left, RightGutter - 4f, fontSize, ProfilerPalette.Muted);
        DrawString(font, new Vector2(plot.Position.X, size.Y - 3f), "-10 s", HorizontalAlignment.Left, -1, fontSize, ProfilerPalette.Muted);
        DrawString(font, new Vector2(plot.End.X - 26f, size.Y - 3f), "now", HorizontalAlignment.Left, -1, fontSize, ProfilerPalette.Muted);

        // Series.
        double oldest = _now - WindowSeconds;
        float prevX = 0f, prevMs = 0f, prevVoice = 0f;
        bool hasPrev = false;
        for (int i = 0; i < _count; i++)
        {
            int index = (_head + i) % Capacity;
            double t = _time[index];
            if (t < oldest) continue;
            float x = plot.Position.X + (float)((t - oldest) / WindowSeconds) * plot.Size.X;
            float yMs = plot.End.Y - Mathf.Clamp(_ms[index] / _msScale, 0f, 1f) * plot.Size.Y;
            float yVoice = plot.End.Y - Mathf.Clamp(_voices[index] / (float)_voiceScale, 0f, 1f) * plot.Size.Y;
            if (hasPrev)
            {
                DrawLine(new Vector2(prevX, prevVoice), new Vector2(x, prevVoice), ProfilerPalette.Series2, 1.5f);
                DrawLine(new Vector2(x, prevVoice), new Vector2(x, yVoice), ProfilerPalette.Series2, 1.5f);
                DrawLine(new Vector2(prevX, prevMs), new Vector2(x, yMs), ProfilerPalette.Accent, 1.5f);
            }
            prevX = x; prevMs = yMs; prevVoice = yVoice;
            hasPrev = true;
        }

        // Legend with current values.
        DrawString(font, new Vector2(plot.Position.X + 6f, fontSize + 1f), _msLabel, HorizontalAlignment.Left, -1, fontSize, ProfilerPalette.Accent);
        DrawString(font, new Vector2(plot.Position.X + 110f, fontSize + 1f), _voiceLabel, HorizontalAlignment.Left, -1, fontSize, ProfilerPalette.Series2);
        DrawString(font, new Vector2(plot.End.X - 80f, fontSize + 1f), "update / voices", HorizontalAlignment.Left, -1, fontSize, ProfilerPalette.Muted);
    }
}
