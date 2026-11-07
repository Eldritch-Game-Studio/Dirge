using System.Collections.Generic;

namespace Dirge.Project;

public enum CurveShape { Linear, Smooth, Stepped }

public readonly record struct CurvePoint(float X, float Y, CurveShape ShapeToNext = CurveShape.Linear);

/// <summary>Piecewise curve mapping a parameter value (X) to a property value (Y). Points sorted by X; clamps outside.</summary>
public sealed class Curve
{
    public List<CurvePoint> Points { get; } = new();
}

/// <summary>Property driven by a parameter through a curve.</summary>
public sealed class AutomationLane
{
    public DirgeId Id { get; set; } = DirgeId.New();
    public PropertyRef Target { get; set; }
    public DirgeId Parameter { get; set; }
    public Curve Curve { get; set; } = new();
}

/// <summary>Attack/hold/decay/sustain/release envelope in seconds and linear 0..1 levels.</summary>
public sealed class Ahdsr
{
    public float Attack { get; set; }
    public float Hold { get; set; }
    public float Decay { get; set; }
    public float Sustain { get; set; } = 1f;
    public float Release { get; set; }
}

public abstract class Modulator
{
    public DirgeId Id { get; set; } = DirgeId.New();
    public PropertyRef Target { get; set; }
    /// <summary>Peak deviation added to the property, in the property's units (dB for volumes, ratio for pitch).</summary>
    public float Amount { get; set; }
}

/// <summary>Fixed random offset chosen when the owning instance/instrument starts.</summary>
public sealed class RandomModulator : Modulator { }

/// <summary>Envelope from the owning instance start; release begins at Stop/KeyOff.</summary>
public sealed class AhdsrModulator : Modulator
{
    public Ahdsr Envelope { get; set; } = new();
}

public enum LfoShape { Sine, Square, Triangle, SawUp, SawDown, Noise }

public sealed class LfoModulator : Modulator
{
    public LfoShape Shape { get; set; } = LfoShape.Sine;
    public float RateHz { get; set; } = 1f;
    public float Phase { get; set; }
    /// <summary>When true, <see cref="RateHz"/> is interpreted in beats (1 = one beat) using the event tempo map.</summary>
    public bool TempoSync { get; set; }
}
