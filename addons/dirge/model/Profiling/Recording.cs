using System.Collections.Generic;
using Dirge.Runtime;

namespace Dirge.Profiling;

/// <summary>
/// Everything needed to reproduce a <see cref="DirgeSystem"/> session: the project identity and seed it was built with,
/// and one <see cref="RecordedTick"/> per <see cref="DirgeSystem.Update"/> holding the API calls issued since the previous
/// update and the command stream the update emitted. Produced by <see cref="Recorder"/>, consumed by <see cref="Replayer"/>
/// and the profiler pane.
/// </summary>
public sealed class Recording
{
    public string ProjectName { get; set; } = "";
    public int Seed { get; set; }
    public string? Platform { get; set; }
    public string Locale { get; set; } = "";
    public List<RecordedTick> Ticks { get; } = new();
}

public sealed class RecordedTick
{
    public double Dt { get; set; }
    /// <summary>System time at the start of the tick (before <see cref="Dt"/> is added).</summary>
    public double Time { get; set; }
    /// <summary>API calls issued between the previous update and this one, in order.</summary>
    public List<ApiCall> Calls { get; } = new();
    /// <summary>Programmer-sound resolutions made during this update (callback answers included), in order.</summary>
    public List<ProgrammerSoundRecord> ProgrammerSounds { get; } = new();
    /// <summary><see cref="Command"/> records emitted by the update, as <see cref="CommandText"/> text.</summary>
    public List<string> Commands { get; } = new();
    /// <summary>Handles of instances that reached Stopped/destroyed and snapshots that finished during this update (one-shots end without any API call).</summary>
    public List<int> Ended { get; } = new();
    public int InstanceCount { get; set; }
    public int RealVoiceCount { get; set; }
}

/// <summary>
/// One public API call. <see cref="Handle"/> identifies the instance/snapshot it was made on (assigned by the recorder when
/// <c>CreateInstance</c>/<c>StartSnapshot</c> returned it; stored in <see cref="Result"/> on that creating call). Argument
/// slots are generic: <see cref="Name"/> (event path, parameter, bus, VCA, asset, feedback kind), <see cref="Text"/> (label,
/// failure reason), <see cref="Value"/> (number), <see cref="Flag"/> (immediate/paused/mute/solo), <see cref="Mode"/>
/// (<see cref="StopMode"/>, listener index), <see cref="Vector"/> (<see cref="Attributes3D"/> as 12 floats) and <see cref="Region"/> (exact integer identity).
/// </summary>
public sealed class ApiCall
{
    public string Method { get; set; } = "";
    public ApiTarget Target { get; set; }
    public int Handle { get; set; } = -1;
    public int Result { get; set; } = -1;
    public string? Name { get; set; }
    public string? Text { get; set; }
    public double Value { get; set; }
    public bool Flag { get; set; }
    public int Mode { get; set; }
    public float[]? Vector { get; set; }
    /// <summary>Spatial region, kept as an integer (never packed into lossy float coordinates). Absent in legacy recordings = 0.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int Region { get; set; }

    internal static float[] Pack(in Attributes3D a) => new[]
    {
        a.Position.X, a.Position.Y, a.Position.Z,
        a.Velocity.X, a.Velocity.Y, a.Velocity.Z,
        a.Forward.X, a.Forward.Y, a.Forward.Z,
        a.Up.X, a.Up.Y, a.Up.Z,
    };

    internal static Attributes3D Unpack(float[]? v, int region = 0)
    {
        if (v is null || v.Length < 12) return Attributes3D.Origin with { Region = new SpatialRegionId(region) };
        return new Attributes3D(
            new System.Numerics.Vector3(v[0], v[1], v[2]),
            new System.Numerics.Vector3(v[3], v[4], v[5]),
            new System.Numerics.Vector3(v[6], v[7], v[8]),
            new System.Numerics.Vector3(v[9], v[10], v[11]), new SpatialRegionId(region));
    }
}

/// <summary>A programmer key resolved for instance <see cref="Handle"/> during an update; <see cref="Asset"/> may be empty.</summary>
public sealed class ProgrammerSoundRecord
{
    public int Handle { get; set; } = -1;
    public string Key { get; set; } = "";
    public string Asset { get; set; } = "";
}

/// <summary>First point where a replay's command stream departs from the recording (see <see cref="Replayer.Compare"/>).</summary>
public readonly record struct Divergence(int Tick, int Index, string Expected, string Actual);
