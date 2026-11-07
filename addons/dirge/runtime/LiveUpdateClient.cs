using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using Dirge.Project;
using Dirge.Serialization;
using Godot;

namespace Dirge.Driver;

/// <summary>
/// Editor side of live update: connects to a game running <see cref="LiveUpdateHost"/>, decodes its <c>hello</c>/<c>stats</c>/
/// <c>instances</c> stream and pushes project/mixer/transport requests. Polled from <c>_Process</c> (add it to the tree).
/// Never reconnects on its own: call <see cref="Connect(string, int)"/> again after <see cref="Disconnected"/>.
/// </summary>
public partial class LiveUpdateClient : Node
{
    public enum ConnectionState { Disconnected, Connecting, Connected }

    private readonly LiveFrameReader _reader = new();
    private readonly ArrayBufferWriter<byte> _out = new(4096);
    private readonly List<LiveInstance> _instances = new();
    private StreamPeerTcp? _peer;
    private ConnectionState _state;
    private double _connectElapsed;

    /// <summary>Seconds a connection attempt may take before it is reported as failed.</summary>
    [Export]
    public double ConnectTimeout { get; set; } = 5.0;

    public ConnectionState State => _state;
    /// <summary>TCP connected and <c>hello</c> received.</summary>
    public new bool IsConnected => _state == ConnectionState.Connected && _hello is not null;
    public string Host { get; private set; } = "127.0.0.1";
    public int Port { get; private set; } = LiveProtocol.DefaultPort;

    private HelloMessage? _hello;
    /// <summary>Last <c>hello</c>; null until the game answered.</summary>
    public HelloMessage? HelloInfo => _hello;
    /// <summary>Most recent stats; null until the first arrive.</summary>
    public LiveStats? LastStats { get; private set; }
    /// <summary>Most recent instance list (reused list; copy if you keep it).</summary>
    public IReadOnlyList<LiveInstance> LastInstances => _instances;

    /// <summary>Game accepted us: project name.</summary>
    public event Action<string>? Hello;
    public event Action<LiveStats>? Stats;
    public event Action<IReadOnlyList<LiveInstance>>? Instances;
    /// <summary>Handle allocated by the game for a <see cref="SendStart"/>; 0 = unknown event.</summary>
    public event Action<int, string>? Started;
    /// <summary>Game rejected a request (connection stays up).</summary>
    public event Action<string>? ErrorReceived;
    /// <summary>Connection ended or failed; the reason is human-readable.</summary>
    public event Action<string>? Disconnected;
    /// <summary>Every decoded message, after the typed events.</summary>
    public event Action<LiveMessage>? MessageReceived;

    /// <summary>Starts connecting. Returns the socket error for an immediate failure; progress is reported through events.</summary>
    public Error Connect(string host = "127.0.0.1", int port = LiveProtocol.DefaultPort)
    {
        Disconnect();
        Host = host;
        Port = port;
        var peer = new StreamPeerTcp();
        Error error = peer.ConnectToHost(host, port);
        if (error != Error.Ok)
        {
            Disconnected?.Invoke($"connect to {host}:{port} failed: {error}");
            return error;
        }

        peer.SetNoDelay(true);
        _peer = peer;
        _state = ConnectionState.Connecting;
        _connectElapsed = 0;
        _reader.Reset();
        return Error.Ok;
    }

    /// <summary>Closes the socket quietly (no <see cref="Disconnected"/> event).</summary>
    public void Disconnect()
    {
        _peer?.DisconnectFromHost();
        _peer?.Dispose();
        _peer = null;
        _hello = null;
        _state = ConnectionState.Disconnected;
        _reader.Reset();
    }

    public override void _ExitTree()
    {
        Disconnect();
    }

    public override void _Process(double delta)
    {
        if (_peer is null)
        {
            return;
        }

        _peer.Poll();
        StreamPeerTcp.Status status = _peer.GetStatus();
        switch (status)
        {
            case StreamPeerTcp.Status.Connecting:
                _connectElapsed += delta;
                if (_connectElapsed > ConnectTimeout)
                {
                    Drop($"connect to {Host}:{Port} timed out");
                }

                return;
            case StreamPeerTcp.Status.None:
            case StreamPeerTcp.Status.Error:
                Drop(_state == ConnectionState.Connecting ? $"connect to {Host}:{Port} refused" : "connection closed");
                return;
        }

        _state = ConnectionState.Connected;
        int available = _peer.GetAvailableBytes();
        if (available > 0)
        {
            Godot.Collections.Array result = _peer.GetPartialData(available);
            var error = (Error)result[0].AsInt32();
            if (error != Error.Ok)
            {
                Drop($"read failed: {error}");
                return;
            }

            if (!_reader.Append(result[1].AsByteArray()))
            {
                Drop("frame too large");
                return;
            }

            while (_peer is not null && _reader.TryReadFrame(out ReadOnlySpan<byte> frame))
            {
                Dispatch(frame);
            }
        }
    }

    private void Dispatch(ReadOnlySpan<byte> frame)
    {
        LiveMessage message;
        try
        {
            message = LiveProtocol.Decode(frame);
        }
        catch (JsonException e)
        {
            GD.PushWarning($"Dirge live: dropped malformed message: {e.Message}");
            return;
        }

        switch (message)
        {
            case HelloMessage hello:
                _hello = hello;
                Hello?.Invoke(hello.ProjectName);
                break;
            case LiveStats stats:
                LastStats = stats;
                Stats?.Invoke(stats);
                break;
            case InstancesMessage instances:
                _instances.Clear();
                _instances.AddRange(instances.Instances);
                Instances?.Invoke(_instances);
                break;
            case StartedMessage started:
                Started?.Invoke(started.Handle, started.Path);
                break;
            case ErrorMessage error:
                ErrorReceived?.Invoke(error.Message);
                break;
        }

        MessageReceived?.Invoke(message);
    }

    private void Drop(string reason)
    {
        Disconnect();
        Disconnected?.Invoke(reason);
    }

    // ---- requests -----------------------------------------------------------------------------------------------

    /// <summary>Sends any message. False when not connected (message dropped).</summary>
    public bool Send(LiveMessage message)
    {
        if (_peer is null || _state != ConnectionState.Connected)
        {
            return false;
        }

        _out.Clear();
        LiveProtocol.Encode(message, _out);
        Error error = _peer.PutData(_out.WrittenSpan.ToArray());
        if (error != Error.Ok)
        {
            Drop($"write failed: {error}");
            return false;
        }

        return true;
    }

    /// <summary>Full project replace in the game (the game hot-reloads; see <see cref="DirgeServer.HotReload"/>).</summary>
    public bool SendProject(AudioProject project) => Send(new ProjectMessage(ProjectJson.Serialize(project, indented: false)));

    public bool SendProjectJson(string json) => Send(new ProjectMessage(json));

    /// <summary>Pushes an authored bus fader value (absolute dB).</summary>
    public bool SendBusVolume(string bus, float volumeDb) => Send(new MixerSetMessage(bus, VolumeDb: volumeDb));

    /// <summary>Pushes one float property of an authored effect (chain index as authored).</summary>
    public bool SendEffectParameter(string bus, int effectIndex, string name, float value)
        => Send(new MixerSetMessage(bus, Effect: effectIndex, Name: name, Value: value));

    public bool SendStart(string path) => Send(new StartMessage(path));

    public bool SendStop(int handle, bool immediate = false) => Send(new StopMessage(handle, immediate));

    /// <summary>Instance parameter when <paramref name="handle"/> is non-zero, otherwise a global parameter.</summary>
    public bool SendParameter(string name, float value, int handle = 0) => Send(new SetParamMessage(name, value, handle == 0 ? null : handle));

    public bool SendSnapshot(string name, bool start = true) => Send(new SnapshotMessage(name, start));
}
