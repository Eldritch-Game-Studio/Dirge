using System.Collections.Generic;

namespace Dirge.Project;

public sealed class MixerGraph
{
    /// <summary>Buses in any order. Exactly one has <see cref="Bus.Parent"/> == None and is the master; its <see cref="Bus.Name"/> must be "Master".</summary>
    public List<Bus> Buses { get; } = new();
    public List<Vca> Vcas { get; } = new();
}

public sealed class Bus
{
    public DirgeId Id { get; set; } = DirgeId.New();
    public string Name { get; set; } = "";
    public DirgeId Parent { get; set; } = DirgeId.None;
    public float VolumeDb { get; set; }
    public bool Mute { get; set; }
    public bool Solo { get; set; }
    public bool BypassEffects { get; set; }
    /// <summary>Effect chain in order. Index is the runtime <c>AudioServer</c> effect index.</summary>
    public List<BusEffect> Effects { get; } = new();
    /// <summary>VCAs whose volume is summed (in dB) onto this bus.</summary>
    public List<DirgeId> Vcas { get; } = new();
    /// <summary>Hard cap on simultaneous real voices routed here; 0 = unlimited.</summary>
    public int MaxVoices { get; set; }
}

/// <summary>An effect on a bus. <see cref="TypeName"/> is the engine class (e.g. "AudioEffectCompressor"); parameters are engine property names.</summary>
public sealed class BusEffect
{
    public DirgeId Id { get; set; } = DirgeId.New();
    public string TypeName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public Dictionary<string, float> Parameters { get; } = new();
    /// <summary>String-valued engine properties (e.g. compressor sidechain bus name).</summary>
    public Dictionary<string, string> StringParameters { get; } = new();
}

public sealed class Vca
{
    public DirgeId Id { get; set; } = DirgeId.New();
    public string Name { get; set; } = "";
    public float VolumeDb { get; set; }
}

/// <summary>Set of mixer property overrides blended in by intensity. Higher <see cref="Priority"/> wins on conflict.</summary>
public sealed class Snapshot
{
    public DirgeId Id { get; set; } = DirgeId.New();
    public string Name { get; set; } = "";
    public int Priority { get; set; }
    /// <summary>Blending: scoped values are interpolated towards by intensity. Overriding: a started snapshot fully replaces lower-priority values.</summary>
    public SnapshotMode Mode { get; set; } = SnapshotMode.Blending;
    public Ahdsr Intensity { get; set; } = new();
    public List<SnapshotValue> Values { get; } = new();
}

public enum SnapshotMode { Blending, Overriding }

public sealed class SnapshotValue
{
    public PropertyRef Target { get; set; }
    public float Value { get; set; }
}
