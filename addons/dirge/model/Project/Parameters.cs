using System.Collections.Generic;

namespace Dirge.Project;

public enum ParameterKind { Continuous, Discrete, Labeled }

/// <summary>Engine-fed parameters computed from listener and emitter attributes each tick.</summary>
public enum BuiltInParameter
{
    None,
    Distance,
    DistanceNormalized,
    Direction,
    Elevation,
    EventConeAngle,
    EventOrientation,
    Speed,
    SpeedAbsolute,
}

public sealed class ParameterDefinition
{
    public DirgeId Id { get; set; } = DirgeId.New();
    public string Name { get; set; } = "";
    public ParameterKind Kind { get; set; } = ParameterKind.Continuous;
    public BuiltInParameter BuiltIn { get; set; } = BuiltInParameter.None;
    public float Minimum { get; set; }
    public float Maximum { get; set; } = 1f;
    public float Default { get; set; }
    /// <summary>Units per second the effective value moves toward the target when rising; 0 = instant.</summary>
    public float SeekSpeedUp { get; set; }
    /// <summary>Units per second when falling; 0 = instant.</summary>
    public float SeekSpeedDown { get; set; }
    /// <summary>Hold the effective value at the target when the instance stops being driven (no decay back to default).</summary>
    public bool Hold { get; set; } = true;
    /// <summary>Labels for <see cref="ParameterKind.Labeled"/>; index = value.</summary>
    public List<string> Labels { get; } = new();
}
