using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using Dirge.Project;
using Dirge.Runtime;
using Dirge.Serialization;
using Godot;

namespace Dirge.Driver;

/// <summary>
/// Game side of live update. Listens on 127.0.0.1:<see cref="Port"/> for one editor at a time (a second connection replaces
/// the first), greets it with <c>hello</c>, streams <c>stats</c> at 10 Hz and <c>instances</c> at 5 Hz and applies the
/// editor's requests to the owning <see cref="DirgeServer"/>. Everything runs on the main thread from <c>_Process</c>, which
/// runs after the server's own <c>_Process</c> because the host is its child, so a frame's stats describe that frame.
/// Created by <see cref="DirgeServer"/> when <see cref="DirgeServer.LiveUpdatePort"/> or <c>--dirge-live[=port]</c> asks for it.
/// </summary>
public partial class LiveUpdateHost : Node
{
    private const double StatsInterval = 0.1;
    private const double InstancesInterval = 0.2;

    private DirgeServer _server = null!;
    private TcpServer? _listener;
    private readonly LiveFrameReader _reader = new();
    private readonly ArrayBufferWriter<byte> _out = new(4096);
    private readonly List<LiveInstance> _instances = new();
    private StreamPeerTcp? _client;
    private double _statsTimer;
    private double _instancesTimer;

    public int Port { get; private set; }
    public bool IsListening => _listener is not null && _listener.IsListening();
    public bool HasClient => _client is not null;

    /// <summary>Every request after it was applied (or rejected with an <see cref="ErrorMessage"/>).</summary>
    public event Action<LiveMessage>? RequestHandled;
    public event Action? ClientConnected;
    public event Action? ClientDisconnected;

    /// <summary>The server whose state is published; defaults to the parent node when added under a <see cref="DirgeServer"/>.</summary>
    public DirgeServer? Server
    {
        get => _server;
        set => _server = value!;
    }

    public LiveUpdateHost()
    {
        Name = "LiveUpdateHost";
    }

    public LiveUpdateHost(DirgeServer server) : this()
    {
        _server = server;
    }

    public override void _EnterTree()
    {
        if (_server is null && GetParent() is DirgeServer parent)
        {
            _server = parent;
        }
    }

    /// <summary>Binds the loopback port. Fails (logged) when the port is taken.</summary>
    public Error Listen(int port)
    {
        Close();
        _listener ??= new TcpServer();
        Error error = _listener.Listen((ushort)port, "127.0.0.1");
        if (error != Error.Ok)
        {
            GD.PushError($"Dirge live: cannot listen on 127.0.0.1:{port}: {error}.");
            return error;
        }

        Port = port;
        GD.Print($"Dirge live: listening on 127.0.0.1:{port}.");
        return Error.Ok;
    }

    public void Close()
    {
        DropClient();
        if (_listener is not null && _listener.IsListening())
        {
            _listener.Stop();
        }
    }

    public override void _ExitTree()
    {
        Close();
        // Managed RefCounted wrappers are reported as leaked at exit unless disposed explicitly.
        _listener?.Dispose();
        _listener = null;
    }

    public override void _Process(double delta)
    {
        if (_server is null)
        {
            return;
        }

        if (_listener is not null && _listener.IsConnectionAvailable())
        {
            Accept(_listener.TakeConnection());
        }

        if (_client is null)
        {
            return;
        }

        _client.Poll();
        if (_client.GetStatus() != StreamPeerTcp.Status.Connected)
        {
            DropClient();
            return;
        }

        Receive();
        if (_client is null)
        {
            return;
        }

        _statsTimer += delta;
        if (_statsTimer >= StatsInterval)
        {
            _statsTimer = 0;
            SendStats();
        }

        _instancesTimer += delta;
        if (_client is not null && _instancesTimer >= InstancesInterval)
        {
            _instancesTimer = 0;
            SendInstances();
        }
    }

    private void Accept(StreamPeerTcp peer)
    {
        if (_client is not null)
        {
            GD.Print("Dirge live: new editor connection replaces the previous one.");
            DropClient();
        }

        peer.SetNoDelay(true);
        _client = peer;
        _reader.Reset();
        _statsTimer = StatsInterval;
        _instancesTimer = InstancesInterval;
        GD.Print($"Dirge live: editor connected from {peer.GetConnectedHost()}:{peer.GetConnectedPort()}.");
        Send(new HelloMessage(DirgeServer.Version, _server.IsLoaded ? _server.System.Project.Name : ""));
        ClientConnected?.Invoke();
    }

    private void DropClient()
    {
        if (_client is null)
        {
            return;
        }

        _client.DisconnectFromHost();
        _client.Dispose();
        _client = null;
        _reader.Reset();
        GD.Print("Dirge live: editor disconnected.");
        ClientDisconnected?.Invoke();
    }

    private void Receive()
    {
        int available = _client!.GetAvailableBytes();
        if (available <= 0)
        {
            return;
        }

        Godot.Collections.Array result = _client.GetPartialData(available);
        var error = (Error)result[0].AsInt32();
        if (error != Error.Ok)
        {
            DropClient();
            return;
        }

        if (!_reader.Append(result[1].AsByteArray()))
        {
            GD.PushError("Dirge live: frame exceeded the size limit; dropping the editor.");
            DropClient();
            return;
        }

        while (_client is not null && _reader.TryReadFrame(out ReadOnlySpan<byte> frame))
        {
            LiveMessage message;
            try
            {
                message = LiveProtocol.Decode(frame);
            }
            catch (JsonException e)
            {
                Send(new ErrorMessage($"malformed message: {e.Message}"));
                continue;
            }

            Handle(message);
        }
    }

    private void Handle(LiveMessage message)
    {
        try
        {
            switch (message)
            {
                case ProjectMessage project:
                    _server.HotReload(ProjectJson.Deserialize(project.Json));
                    break;
                case MixerSetMessage mixer:
                    HandleMixerSet(mixer);
                    break;
                case StartMessage start:
                    Send(new StartedMessage(_server.StartEvent(start.Path), start.Path));
                    // Short one-shots would be missed by the 5 Hz list; publish it on the next frame.
                    _instancesTimer = InstancesInterval;
                    break;
                case StopMessage stop:
                    _server.StopEvent(stop.Handle, stop.Immediate);
                    _instancesTimer = InstancesInterval;
                    break;
                case SetParamMessage parameter:
                    if (parameter.Handle is > 0)
                    {
                        _server.SetParameter(parameter.Handle.Value, parameter.Name, parameter.Value);
                    }
                    else
                    {
                        _server.SetGlobalParameter(parameter.Name, parameter.Value);
                    }

                    break;
                case SnapshotMessage snapshot:
                    if (snapshot.Start)
                    {
                        _server.StartSnapshot(snapshot.Name);
                    }
                    else
                    {
                        _server.StopSnapshot(snapshot.Name);
                    }

                    break;
                default:
                    Send(new ErrorMessage($"'{message.GetType().Name}' is not a request the game handles."));
                    break;
            }
        }
        catch (Exception e)
        {
            GD.PushError($"Dirge live: request failed: {e.Message}");
            Send(new ErrorMessage(e.Message));
        }

        RequestHandled?.Invoke(message);
    }

    private void HandleMixerSet(MixerSetMessage mixer)
    {
        if (!_server.IsLoaded)
        {
            Send(new ErrorMessage("no project loaded."));
            return;
        }

        if (mixer.VolumeDb.HasValue)
        {
            if (!_server.LiveSetBusVolume(mixer.Bus, mixer.VolumeDb.Value))
            {
                Send(new ErrorMessage($"unknown bus '{mixer.Bus}'."));
            }

            return;
        }

        if (mixer.Effect.HasValue && mixer.Name is not null && mixer.Value.HasValue)
        {
            if (!_server.LiveSetEffectParameter(mixer.Bus, mixer.Effect.Value, mixer.Name, mixer.Value.Value))
            {
                Send(new ErrorMessage($"unknown effect {mixer.Effect.Value} on bus '{mixer.Bus}'."));
            }

            return;
        }

        Send(new ErrorMessage("mixer-set needs {bus, volumeDb} or {bus, effect, name, value}."));
    }

    private void SendStats()
    {
        DirgeStats stats = _server.Stats;
        IReadOnlyList<string> buses = _server.Driver.BusNames;
        var levels = new LiveBusLevel[buses.Count];
        for (int i = 0; i < buses.Count; i++)
        {
            int index = _server.Driver.BusIndex(buses[i]);
            levels[i] = new LiveBusLevel(
                buses[i],
                AudioServer.GetBusPeakVolumeLeftDb(index, 0),
                AudioServer.GetBusPeakVolumeRightDb(index, 0));
        }

        Send(new LiveStats(
            stats.Time,
            stats.Instances,
            stats.RealVoices,
            stats.LastUpdateMs,
            levels,
            stats.MomentaryLufs,
            stats.ShortTermLufs,
            stats.IntegratedLufs,
            stats.TruePeakDbtp));
    }

    private void SendInstances()
    {
        _instances.Clear();
        foreach (KeyValuePair<int, EventInstance> pair in _server.Handles)
        {
            EventInstance instance = pair.Value;
            _instances.Add(new LiveInstance(pair.Key, instance.Description.Path, instance.State.ToString(), instance.TimelinePosition, instance.IsVirtual));
        }

        Send(new InstancesMessage(_instances));
    }

    private void Send(LiveMessage message)
    {
        if (_client is null)
        {
            return;
        }

        _out.Clear();
        LiveProtocol.Encode(message, _out);
        Error error = _client.PutData(_out.WrittenSpan.ToArray());
        if (error != Error.Ok)
        {
            DropClient();
        }
    }
}
