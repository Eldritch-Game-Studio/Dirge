using System.Collections.Generic;

namespace Dirge.Project;

/// <summary>Root of an authored project. Pure data; runtime state lives in <see cref="Dirge.Runtime.DirgeSystem"/>.</summary>
public sealed class AudioProject
{
    public string Name { get; set; } = "";
    public MixerGraph Mixer { get; set; } = new();
    public List<ParameterDefinition> GlobalParameters { get; } = new();
    public List<Snapshot> Snapshots { get; } = new();
    public List<AudioEvent> Events { get; } = new();
    public List<AudioBank> Banks { get; } = new();
    /// <summary>Known asset lengths in seconds, filled by the tool at import time so the model can schedule one-shots without the engine.</summary>
    public Dictionary<string, double> AssetLengths { get; } = new();
}

/// <summary>Preload group: events whose sample data is loaded together.</summary>
public sealed class AudioBank
{
    public DirgeId Id { get; set; } = DirgeId.New();
    public string Name { get; set; } = "";
    public List<DirgeId> Events { get; } = new();
}
