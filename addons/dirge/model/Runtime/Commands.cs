using System.Collections.Generic;
using System.Numerics;
using Dirge.Project;

namespace Dirge.Runtime;

/// <summary>Runtime voice handle. Allocated by the model; the engine driver maps it to a player.</summary>
public readonly record struct VoiceId(int Value)
{
    public override string ToString() => $"v{Value}";
}

/// <summary>3D attributes in engine world space.</summary>
public readonly record struct Attributes3D(Vector3 Position, Vector3 Velocity, Vector3 Forward, Vector3 Up)
{
    public static readonly Attributes3D Origin = new(Vector3.Zero, Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
}

/// <summary>
/// Everything the model asks the engine to do, in order. The driver applies these verbatim to <c>AudioServer</c> and players;
/// the model never touches the engine. The sequence is also the profiler/test oracle.
/// </summary>
public abstract record Command;

public sealed record VoicePlay(VoiceId Voice, AssetRef Asset, string Bus, double StartSeconds, bool Loop, float VolumeDb, float Pitch, bool Spatial, Attributes3D Attributes, float MinDistance, float MaxDistance) : Command;
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
