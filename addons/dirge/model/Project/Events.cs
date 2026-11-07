using System.Collections.Generic;

namespace Dirge.Project;

public enum StealingMode { Oldest, Quietest, Furthest, Virtualize, None }

public sealed class EventMacros
{
    public float VolumeDb { get; set; }
    public float Pitch { get; set; } = 1f;
    /// <summary>Lower number = more important (FMOD convention).</summary>
    public int Priority { get; set; } = 128;
    /// <summary>0 = unlimited.</summary>
    public int MaxInstances { get; set; }
    public StealingMode Stealing { get; set; } = StealingMode.Oldest;
    /// <summary>Seconds after an instance starts before another may; 0 = none.</summary>
    public float Cooldown { get; set; }
    /// <summary>Instance stays alive after its timeline ends until Stop is called.</summary>
    public bool Persistent { get; set; }
    public bool Is3D { get; set; }
    public float MinDistance { get; set; } = 1f;
    public float MaxDistance { get; set; } = 20f;
    public bool Doppler { get; set; }
    public float DopplerScale { get; set; } = 1f;
    /// <summary>Seconds the scheduler is allowed to look ahead when quantizing starts.</summary>
    public float ScheduleLookahead { get; set; }
}

public sealed class AudioEvent
{
    public DirgeId Id { get; set; } = DirgeId.New();
    /// <summary>Slash-separated path, e.g. "Ambience/Wind".</summary>
    public string Path { get; set; } = "";
    public DirgeId Bus { get; set; }
    public EventMacros Macros { get; set; } = new();
    public List<ParameterDefinition> LocalParameters { get; } = new();
    public List<Track> Tracks { get; } = new();
    public List<AutomationLane> Automation { get; } = new();
    public List<Modulator> Modulators { get; } = new();
    public List<LogicMarker> Markers { get; } = new();
    /// <summary>Sorted by <see cref="TempoMarker.Position"/>. Empty = no musical time.</summary>
    public List<TempoMarker> Tempo { get; } = new();
}

public sealed class Track
{
    public DirgeId Id { get; set; } = DirgeId.New();
    public string Name { get; set; } = "";
    public float VolumeDb { get; set; }
    public List<Instrument> Instruments { get; } = new();
}

/// <summary>Where an instrument lives: on the timeline (seconds) or on a parameter sheet (parameter range).</summary>
public sealed class Placement
{
    public DirgeId Parameter { get; set; } = DirgeId.None;
    public bool OnTimeline => Parameter.IsNone;
    /// <summary>Timeline start in seconds, or parameter range start.</summary>
    public float Start { get; set; }
    /// <summary>Timeline length in seconds, or parameter range length. 0 on timeline = instrument's natural length.</summary>
    public float Length { get; set; }
}

public sealed class TriggerCondition
{
    public DirgeId Parameter { get; set; }
    public float Minimum { get; set; }
    public float Maximum { get; set; }
}

public enum CutMode { Cut, FadeOut }

public abstract class Instrument
{
    public DirgeId Id { get; set; } = DirgeId.New();
    public string Name { get; set; } = "";
    public Placement Placement { get; set; } = new();
    public float VolumeDb { get; set; }
    public float Pitch { get; set; } = 1f;
    public float VolumeRandomDb { get; set; }
    public float PitchRandom { get; set; }
    public float StartOffset { get; set; }
    public float FadeIn { get; set; }
    public float FadeOut { get; set; }
    public bool Loop { get; set; }
    /// <summary>Keep playing after leaving the trigger region instead of cutting.</summary>
    public bool Async { get; set; }
    public CutMode CutMode { get; set; } = CutMode.Cut;
    /// <summary>Simultaneous sounds this instrument may own; 0 = unlimited.</summary>
    public int Polyphony { get; set; }
    public float TriggerDelay { get; set; }
    /// <summary>0..1 chance the instrument triggers when its region is entered.</summary>
    public float Probability { get; set; } = 1f;
    public List<TriggerCondition> Conditions { get; } = new();
    public List<Modulator> Modulators { get; } = new();
}

public sealed class ClipInstrument : Instrument
{
    public AssetRef Asset { get; set; }
}

public enum PlaylistMode { Random, Shuffle, Sequential }

public sealed class MultiEntry
{
    public AssetRef Asset { get; set; }
    public float Weight { get; set; } = 1f;
}

public sealed class MultiInstrument : Instrument
{
    public PlaylistMode Mode { get; set; } = PlaylistMode.Random;
    /// <summary>Number of recent picks that may not repeat (Random mode).</summary>
    public int AvoidRepeat { get; set; } = 1;
    public List<MultiEntry> Entries { get; } = new();
}

public sealed class ScattererInstrument : Instrument
{
    public float MinInterval { get; set; } = 0.5f;
    public float MaxInterval { get; set; } = 2f;
    public float MinScatterDistance { get; set; }
    public float MaxScatterDistance { get; set; } = 10f;
    public PlaylistMode Mode { get; set; } = PlaylistMode.Random;
    public List<MultiEntry> Entries { get; } = new();
}

public sealed class SilenceInstrument : Instrument { }

/// <summary>Plays another event as a child instance.</summary>
public sealed class EventInstrument : Instrument
{
    public DirgeId Event { get; set; }
}

/// <summary>Asset supplied at runtime by key (dialogue, localization tables).</summary>
public sealed class ProgrammerInstrument : Instrument
{
    public string Key { get; set; } = "";
}

public enum CommandAction { StartEvent, StopEvent, SetParameter }

public sealed class CommandInstrument : Instrument
{
    public CommandAction Action { get; set; }
    public DirgeId TargetEvent { get; set; }
    public DirgeId TargetParameter { get; set; }
    public float Value { get; set; }
}

/// <summary>Starts a snapshot while the region is active.</summary>
public sealed class SnapshotInstrument : Instrument
{
    public DirgeId Snapshot { get; set; }
}

public enum Quantization { None, Beat, Bar, Marker }

public abstract class LogicMarker
{
    public DirgeId Id { get; set; } = DirgeId.New();
    public string Name { get; set; } = "";
    public float Position { get; set; }
    public List<TriggerCondition> Conditions { get; } = new();
    public float Probability { get; set; } = 1f;
}

public sealed class DestinationMarker : LogicMarker { }

public sealed class LoopRegion : LogicMarker
{
    public float Length { get; set; }
}

/// <summary>Jump to <see cref="Destination"/> when reached and conditions pass.</summary>
public sealed class TransitionMarker : LogicMarker
{
    public DirgeId Destination { get; set; }
    public Quantization Quantization { get; set; } = Quantization.None;
    /// <summary>Optional transition timeline length in seconds played between source and destination.</summary>
    public float TransitionLength { get; set; }
}

/// <summary>Like <see cref="TransitionMarker"/> but armed for the whole region; fires at the next quantization point.</summary>
public sealed class TransitionRegion : LogicMarker
{
    public float Length { get; set; }
    public DirgeId Destination { get; set; }
    public Quantization Quantization { get; set; } = Quantization.Beat;
}

/// <summary>Timeline holds here until KeyOff.</summary>
public sealed class SustainPoint : LogicMarker { }

public sealed class TempoMarker
{
    public float Position { get; set; }
    public float Bpm { get; set; } = 120f;
    public int BeatsPerBar { get; set; } = 4;
}
