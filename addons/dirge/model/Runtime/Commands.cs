using System.Collections.Generic;
using System.Numerics;
using Dirge.Project;

namespace Dirge.Runtime;

/// <summary>Runtime voice handle. Allocated by the model; the engine driver maps it to a player.</summary>
public readonly record struct VoiceId(int Value)
{
    public override string ToString() => $"v{Value}";
}

/// <summary>Application-owned spatial world identity. Zero is the legacy host world.</summary>
public readonly record struct SpatialRegionId(int Value);

/// <summary>3D attributes in region-local world space; omitted region preserves the legacy host world.</summary>
public readonly record struct Attributes3D(Vector3 Position, Vector3 Velocity, Vector3 Forward, Vector3 Up)
{
    public SpatialRegionId Region { get; init; }

    public Attributes3D(Vector3 position, Vector3 velocity, Vector3 forward, Vector3 up, SpatialRegionId region)
        : this(position, velocity, forward, up) => Region = region;

    public static readonly Attributes3D Origin = new(Vector3.Zero, Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
}

/// <summary>
/// Everything the model asks the engine to do, in order. The driver applies these verbatim to <c>AudioServer</c> and players;
/// the model never touches the engine. The sequence is also the profiler/test oracle.
/// </summary>
public abstract record Command;

/// <summary>
/// <paramref name="SyncGroup"/> ≠ 0: the driver starts voices sharing the group (same instance, same track) sample-locked.
/// <paramref name="Rolloff"/>, <paramref name="Doppler"/> and <paramref name="DopplerScale"/> mirror the event macros and only
/// matter when <paramref name="Spatial"/>; doppler is active when <c>Doppler &amp;&amp; DopplerScale &gt; 0</c> (the engine has no scale).
/// </summary>
public sealed record VoicePlay(VoiceId Voice, AssetRef Asset, string Bus, double StartSeconds, bool Loop, float VolumeDb, float Pitch, bool Spatial, Attributes3D Attributes, float MinDistance, float MaxDistance, int SyncGroup = 0, Rolloff Rolloff = Rolloff.Inverse, bool Doppler = false, float DopplerScale = 1f) : Command;
public sealed record VoiceStop(VoiceId Voice) : Command;
public sealed record VoiceSeek(VoiceId Voice, double Seconds) : Command;
public sealed record VoiceSetVolume(VoiceId Voice, float VolumeDb) : Command;
public sealed record VoiceSetPitch(VoiceId Voice, float Pitch) : Command;
public sealed record VoiceSet3D(VoiceId Voice, Attributes3D Attributes) : Command;
public sealed record VoiceSetPaused(VoiceId Voice, bool Paused) : Command;

public sealed record BusSetVolume(string Bus, float VolumeDb) : Command;
public sealed record BusSetMute(string Bus, bool Mute) : Command;
public sealed record BusSetSolo(string Bus, bool Solo) : Command;
public sealed record BusSetBypass(string Bus, bool Bypass) : Command;
public sealed record EffectSetEnabled(string Bus, int EffectIndex, bool Enabled) : Command;
public sealed record EffectSetParameter(string Bus, int EffectIndex, string Parameter, float Value) : Command;

/// <summary>Driver → model feedback. Delivered before the next <c>Update</c>.</summary>
public abstract record Feedback;
public sealed record VoiceFinished(VoiceId Voice) : Feedback;
public sealed record VoiceFailed(VoiceId Voice, string Reason) : Feedback;

/// <summary>Reusable append-only buffer; cleared by the driver after apply.</summary>
public sealed class CommandBuffer
{
    private readonly List<Command> _commands = new(256);
    public IReadOnlyList<Command> Commands => _commands;
    public int Count => _commands.Count;
    public void Add(Command command) => _commands.Add(command);
    public void Clear() => _commands.Clear();
}

/// <summary>Which runtime object an API call was made on.</summary>
public enum ApiTarget { System, Instance, Mixer, Snapshot }

/// <summary>
/// Observer of every public API call and tick of a <see cref="DirgeSystem"/> (installed via <see cref="DirgeSystem.Recorder"/>;
/// implemented by <c>Dirge.Profiling.Recorder</c>). Calls made internally during <see cref="DirgeSystem.Update"/> or on behalf
/// of another API call (stealing, StopAll) are bracketed by <see cref="EnterInternal"/>/<see cref="ExitInternal"/> so a
/// recording contains only what a replay must reissue. All hooks are no-ops in cost when no sink is attached.
/// </summary>
public interface IApiSink
{
    /// <summary>A new <see cref="EventInstance"/> (<paramref name="name"/> = event path) or <see cref="SnapshotInstance"/> (snapshot name) handed out by the API.</summary>
    void Created(object target, string method, string name);
    /// <summary><paramref name="target"/>: null = system, <see cref="MixerState"/>, <see cref="EventInstance"/> or <see cref="SnapshotInstance"/>.</summary>
    void Call(object? target, string method, string? name, string? text, double value, bool flag, int mode);
    void Call3D(object? target, string method, int index, in Attributes3D attributes, float weight);
    /// <summary>A programmer-sound callback (or <c>SetProgrammerSound</c>/audio-table fallback) resolved <paramref name="key"/> during Update.</summary>
    void ProgrammerSound(EventInstance instance, string key, AssetRef asset);
    void EnterInternal();
    void ExitInternal();
    void BeginUpdate(DirgeSystem system, double dt);
    void EndUpdate(DirgeSystem system);
}
