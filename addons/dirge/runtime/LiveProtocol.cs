using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dirge.Driver;

/// <summary>
/// Live-update wire format shared by <see cref="LiveUpdateHost"/> (game) and <see cref="LiveUpdateClient"/> (editor):
/// newline-delimited UTF-8 JSON objects, each with a <c>type</c> discriminator (first property) and <c>protocol: 1</c>.
/// Property names are camelCase. Non-finite floats (silence is −∞ LUFS) are written as the named literals
/// <c>"-Infinity"</c>/<c>"NaN"</c>, which both ends accept.
/// </summary>
public static class LiveProtocol
{
    public const int Version = 1;
    public const int DefaultPort = 7447;
    public const byte Delimiter = (byte)'\n';
    /// <summary>A line longer than this is rejected and the connection dropped (a project is a few hundred KB at most).</summary>
    public const int MaxFrameBytes = 64 * 1024 * 1024;

    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Appends one framed message (JSON + <c>'\n'</c>) to <paramref name="writer"/>.</summary>
    public static void Encode(LiveMessage message, IBufferWriter<byte> writer)
    {
        using var json = new Utf8JsonWriter(writer);
        JsonSerializer.Serialize(json, message, Options);
        json.Flush();
        Span<byte> tail = writer.GetSpan(1);
        tail[0] = Delimiter;
        writer.Advance(1);
    }

    /// <summary>One framed message as a fresh byte array.</summary>
    public static byte[] Encode(LiveMessage message)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        Encode(message, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Parses one line (without the delimiter). Throws <see cref="JsonException"/> on malformed or unknown messages.</summary>
    public static LiveMessage Decode(ReadOnlySpan<byte> line)
    {
        LiveMessage? message = JsonSerializer.Deserialize<LiveMessage>(line, Options);
        if (message is null)
        {
            throw new JsonException("live: empty message.");
        }

        if (message.Protocol != Version)
        {
            throw new JsonException($"live: protocol {message.Protocol} not supported (expected {Version}).");
        }

        return message;
    }
}

/// <summary>Base of every wire message. The discriminator is the <c>type</c> property.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(LiveStats), "stats")]
[JsonDerivedType(typeof(InstancesMessage), "instances")]
[JsonDerivedType(typeof(StartedMessage), "started")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
[JsonDerivedType(typeof(ProjectMessage), "project")]
[JsonDerivedType(typeof(MixerSetMessage), "mixer-set")]
[JsonDerivedType(typeof(StartMessage), "start")]
[JsonDerivedType(typeof(StopMessage), "stop")]
[JsonDerivedType(typeof(SetParamMessage), "set-param")]
[JsonDerivedType(typeof(SnapshotMessage), "snapshot")]
public abstract record LiveMessage
{
    public int Protocol { get; init; } = LiveProtocol.Version;
}

// ---- game → editor ----------------------------------------------------------------------------------------------

/// <summary>First message after the connection is accepted.</summary>
public sealed record HelloMessage(string Version, string ProjectName) : LiveMessage;

/// <summary>Per-bus engine peak levels in dB (−80 ≈ silence). Wire name of <see cref="Bus"/> is <c>name</c>.</summary>
public sealed record LiveBusLevel([property: JsonPropertyName("name")] string Bus, float Left, float Right);

/// <summary>Runtime statistics, sent at 10 Hz. Loudness fields are <c>-Infinity</c> while the meter has heard only silence.</summary>
public sealed record LiveStats(
    double Time,
    int Instances,
    int RealVoices,
    double UpdateMs,
    IReadOnlyList<LiveBusLevel> Buses,
    float MomentaryLufs,
    float ShortTermLufs,
    float IntegratedLufs,
    float TruePeakDbtp) : LiveMessage;

/// <summary>One handle-tracked instance. <see cref="State"/> is the <c>PlaybackState</c> name.</summary>
public sealed record LiveInstance(int Handle, string Event, string State, double Position, bool Virtual);

/// <summary>Handle-tracked instances, sent at 5 Hz.</summary>
public sealed record InstancesMessage(IReadOnlyList<LiveInstance> Instances) : LiveMessage;

/// <summary>Reply to <see cref="StartMessage"/>: the handle allocated for the new instance (0 = unknown event).</summary>
public sealed record StartedMessage(int Handle, string Path) : LiveMessage;

/// <summary>A request could not be applied; the connection stays open.</summary>
public sealed record ErrorMessage(string Message) : LiveMessage;

// ---- editor → game ----------------------------------------------------------------------------------------------

/// <summary>Full project replace: <see cref="Json"/> is <c>ProjectJson.Serialize</c> output.</summary>
public sealed record ProjectMessage(string Json) : LiveMessage;

/// <summary>
/// Live value push. Either <c>{bus, volumeDb}</c> (authored fader value, absolute dB) or <c>{bus, effect, name, value}</c>
/// (effect chain index as authored and a float property of that effect).
/// </summary>
public sealed record MixerSetMessage(string Bus, float? VolumeDb = null, int? Effect = null, string? Name = null, float? Value = null) : LiveMessage;

public sealed record StartMessage(string Path) : LiveMessage;

public sealed record StopMessage(int Handle, bool Immediate = false) : LiveMessage;

/// <summary>Sets a parameter on a handle, or a global parameter when <see cref="Handle"/> is null/0.</summary>
public sealed record SetParamMessage(string Name, float Value, int? Handle = null) : LiveMessage;

/// <summary>Starts (<see cref="Start"/> true) or stops every running instance of a snapshot by name.</summary>
public sealed record SnapshotMessage(string Name, bool Start = true) : LiveMessage;

/// <summary>
/// Splits a byte stream into delimited frames without copying more than once: bytes are appended, complete lines are
/// handed to <see cref="TryReadFrame"/>. One instance per connection.
/// </summary>
public sealed class LiveFrameReader
{
    private byte[] _buffer = new byte[4096];
    private int _length;
    private int _scanned;

    public int Pending => _length;

    public void Reset()
    {
        _length = 0;
        _scanned = 0;
    }

    /// <summary>Appends received bytes. Returns false (and resets) when a single frame exceeds <see cref="LiveProtocol.MaxFrameBytes"/>.</summary>
    public bool Append(ReadOnlySpan<byte> bytes)
    {
        if (_length + bytes.Length > LiveProtocol.MaxFrameBytes)
        {
            Reset();
            return false;
        }

        if (_length + bytes.Length > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + bytes.Length));
        }

        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
        return true;
    }

    /// <summary>Next complete line (without delimiter); valid until the next call. False when none is complete.</summary>
    public bool TryReadFrame(out ReadOnlySpan<byte> frame)
    {
        // Drop the previously returned frame first.
        if (_scanned > 0)
        {
            Array.Copy(_buffer, _scanned, _buffer, 0, _length - _scanned);
            _length -= _scanned;
            _scanned = 0;
        }

        int newline = Array.IndexOf(_buffer, LiveProtocol.Delimiter, 0, _length);
        if (newline < 0)
        {
            frame = default;
            return false;
        }

        frame = _buffer.AsSpan(0, newline);
        _scanned = newline + 1;
        return true;
    }
}
