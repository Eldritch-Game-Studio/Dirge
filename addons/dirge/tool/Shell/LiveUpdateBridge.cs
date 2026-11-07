#nullable enable
#if TOOLS
using System;
using Dirge.Driver;
using Dirge.Project;
using Dirge.Tool.Core;
using Godot;

namespace Dirge.Tool.Shell;

/// <summary>
/// Editor side of live update: owns the <see cref="LiveUpdateClient"/> node (child of the main screen, name
/// <see cref="NodeName"/> so panes can find it through <c>ToolContext.Host</c>), mirrors edits to the game while connected
/// (value-only bus edits as <c>mixer-set</c>, everything else as a debounced full <c>project</c> replace) and formats the
/// status-bar readout from the game's <c>stats</c>.
/// </summary>
public sealed class LiveUpdateBridge : IDisposable
{
    public const string NodeName = "LiveUpdateClient";
    private const double ProjectPushDelaySeconds = 0.25;

    private readonly ProjectDocument _document;
    private readonly LiveUpdateClient _client;
    private readonly Node _owner;
    private LiveStats? _stats;
    private double _projectPushIn = -1;
    private bool _disposed;

    public LiveUpdateBridge(Node owner, ProjectDocument document)
    {
        _owner = owner;
        _document = document;
        _client = new LiveUpdateClient { Name = NodeName };
        owner.AddChild(_client);
        _client.Hello += OnHello;
        _client.Stats += OnStats;
        _client.Disconnected += OnDisconnected;
        _client.ErrorReceived += OnErrorReceived;
        _document.Changed += OnChanged;
    }

    public LiveUpdateClient Client => _client;
    public bool IsConnected => _client.IsConnected;
    public string Host => _client.Host;
    public int Port => _client.Port;
    public string LastError { get; private set; } = "";
    public string ProjectName { get; private set; } = "";
    public event Action? StateChanged;

    /// <summary>Status-bar text; empty while disconnected.</summary>
    public string StatusText
    {
        get
        {
            if (!_client.IsConnected)
            {
                return _client.State == LiveUpdateClient.ConnectionState.Connecting ? $"live: connecting to {_client.Host}:{_client.Port}…" : "";
            }

            LiveStats? stats = _stats;
            return stats is null
                ? $"live: {ProjectName}"
                : $"live: {ProjectName} · {stats.Instances} inst · {stats.RealVoices} voices · {stats.UpdateMs:0.00} ms";
        }
    }

    public void Connect(string host, int port)
    {
        LastError = "";
        _stats = null;
        Error error = _client.Connect(host.Length == 0 ? "127.0.0.1" : host, port);
        if (error != Error.Ok)
        {
            LastError = $"Live update: cannot connect to {host}:{port} ({error}).";
        }

        StateChanged?.Invoke();
    }

    public void Disconnect()
    {
        _client.Disconnect();
        _stats = null;
        _projectPushIn = -1;
        StateChanged?.Invoke();
    }

    /// <summary>Call every frame: flushes the debounced project push.</summary>
    public void Tick(double dt)
    {
        if (_projectPushIn < 0)
        {
            return;
        }

        _projectPushIn -= dt;
        if (_projectPushIn < 0 && _client.IsConnected)
        {
            _client.SendProject(_document.Project);
        }
    }

    private void OnChanged(ChangeScope scope, DirgeId subject)
    {
        if (!_client.IsConnected || scope == ChangeScope.Assets)
        {
            return;
        }

        if (scope == ChangeScope.Mixer && TryPushBus(subject))
        {
            return;
        }

        _projectPushIn = ProjectPushDelaySeconds;
    }

    private bool TryPushBus(DirgeId subject)
    {
        foreach (Bus bus in _document.Project.Mixer.Buses)
        {
            if (bus.Id != subject)
            {
                continue;
            }

            _client.SendBusVolume(bus.Name, bus.VolumeDb);
            for (int i = 0; i < bus.Effects.Count; i++)
            {
                foreach (System.Collections.Generic.KeyValuePair<string, float> parameter in bus.Effects[i].Parameters)
                {
                    _client.SendEffectParameter(bus.Name, i, parameter.Key, parameter.Value);
                }
            }

            return true;
        }

        return false;
    }

    private void OnHello(string projectName)
    {
        ProjectName = projectName;
        LastError = "";
        StateChanged?.Invoke();
    }

    private void OnStats(LiveStats stats) => _stats = stats;

    private void OnDisconnected(string reason)
    {
        _stats = null;
        LastError = reason.Length > 0 ? "Live update: " + reason : "";
        StateChanged?.Invoke();
    }

    private void OnErrorReceived(string message)
    {
        LastError = "Live update: " + message;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _document.Changed -= OnChanged;
        _client.Hello -= OnHello;
        _client.Stats -= OnStats;
        _client.Disconnected -= OnDisconnected;
        _client.ErrorReceived -= OnErrorReceived;
        _client.Disconnect();
        if (GodotObject.IsInstanceValid(_client))
        {
            _owner.RemoveChild(_client);
            _client.QueueFree();
        }
    }
}
#endif
