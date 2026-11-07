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
    /// <summary>Programmer-instrument key → locale → asset tables consulted before the programmer-sound callback.</summary>
    public List<AudioTable> AudioTables { get; } = new();
    /// <summary>Per-platform mixer baselines; the matching one replaces authored values when the runtime is built.</summary>
    public List<PlatformOverride> PlatformOverrides { get; } = new();
    /// <summary>Global parameter → mixer property (bus volume/pitch, VCA volume, effect parameter) automation.</summary>
    public List<AutomationLane> MixerAutomation { get; } = new();
    /// <summary>
    /// LFO/Random modulators on mixer properties (bus volume/pitch, VCA volume, effect parameter): tremolo, auto-wobble.
    /// Evaluated by <see cref="Dirge.Runtime.MixerState"/> after automation; AHDSR and autopitch are event-only.
    /// </summary>
    public List<Modulator> MixerModulators { get; } = new();
    /// <summary>Known asset lengths in seconds, filled by the tool at import time so the model can schedule one-shots without the engine.</summary>
    public Dictionary<string, double> AssetLengths { get; } = new();
}

/// <summary>
/// Localized programmer-sound lookup. <see cref="Entries"/> maps a programmer key to its per-locale assets. Resolution uses
/// <see cref="Dirge.Runtime.DirgeSystem.Locale"/> and falls back to the first authored locale of that key (file order; the
/// JSON writer sorts locales ordinally, so after a round-trip that is the ordinally smallest locale code).
/// </summary>
public sealed class AudioTable
{
    public DirgeId Id { get; set; } = DirgeId.New();
    public string Name { get; set; } = "";
    public Dictionary<string, Dictionary<string, AssetRef>> Entries { get; } = new();
}

/// <summary>
/// Mixer values that replace the authored baseline on one platform (matched case-insensitively against the platform string
/// the runtime was built with, e.g. Godot's <c>OS.GetName()</c>). Snapshots and automation apply on top of the override.
/// </summary>
public sealed class PlatformOverride
{
    public string Platform { get; set; } = "";
    public List<SnapshotValue> Values { get; } = new();
}

/// <summary>Preload group: events whose sample data is loaded together.</summary>
public sealed class AudioBank
{
    public DirgeId Id { get; set; } = DirgeId.New();
    public string Name { get; set; } = "";
    public List<DirgeId> Events { get; } = new();
}
