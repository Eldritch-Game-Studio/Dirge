using System;
using Dirge.Project;

namespace Dirge.Runtime;

[Flags]
public enum CallbackMask
{
    None = 0,
    Started = 1 << 0,
    Stopped = 1 << 1,
    TimelineMarker = 1 << 2,
    TimelineBeat = 1 << 3,
    SoundPlayed = 1 << 4,
    SoundStopped = 1 << 5,
    Virtualized = 1 << 6,
    Unvirtualized = 1 << 7,
    Stolen = 1 << 8,
    ProgrammerSound = 1 << 9,
    All = ~0,
}

public abstract record CallbackEvent(CallbackMask Kind);
public sealed record StartedCallback() : CallbackEvent(CallbackMask.Started);
public sealed record StoppedCallback() : CallbackEvent(CallbackMask.Stopped);
public sealed record MarkerCallback(string Name, double Position) : CallbackEvent(CallbackMask.TimelineMarker);
public sealed record BeatCallback(int Bar, int Beat, float Bpm, double Position) : CallbackEvent(CallbackMask.TimelineBeat);
public sealed record SoundPlayedCallback(VoiceId Voice, AssetRef Asset) : CallbackEvent(CallbackMask.SoundPlayed);
public sealed record SoundStoppedCallback(VoiceId Voice) : CallbackEvent(CallbackMask.SoundStopped);
public sealed record VirtualizedCallback() : CallbackEvent(CallbackMask.Virtualized);
public sealed record UnvirtualizedCallback() : CallbackEvent(CallbackMask.Unvirtualized);
public sealed record StolenCallback() : CallbackEvent(CallbackMask.Stolen);
/// <summary>Programmer instrument needs an asset; handler sets <see cref="Asset"/>.</summary>
public sealed record ProgrammerSoundCallback(string Key) : CallbackEvent(CallbackMask.ProgrammerSound)
{
    public AssetRef Asset { get; set; }
}
