#nullable enable
using System;
using System.Collections.Generic;
using Dirge.Driver;
using Dirge.Project;
using Dirge.Runtime;
using Dirge.Tool.Core;

namespace Dirge.Tool.Profiler;

/// <summary>One live instance row as the profiler shows it, independent of where it came from.</summary>
public struct ProfilerInstanceRow
{
    public int Handle;
    public string Event;
    public string State;
    public double Position;
    public bool Virtual;
    /// <summary>Local parameter readout ("Name=1.00 …"); empty when the source has none.</summary>
    public string Parameters;
}

/// <summary>Per-bus engine peak level in dB (−80 = silence).</summary>
public struct ProfilerBusLevel
{
    public string Bus;
    public float Left;
    public float Right;
}

/// <summary>Counters shown above the instance table. Loudness fields are NaN when the source has no meter.</summary>
public struct ProfilerStats
{
    public double Time;
    public int Instances;
    public int RealVoices;
    public double UpdateMs;
    public float MomentaryLufs;
    public float ShortTermLufs;
    public float IntegratedLufs;
    public float TruePeakDbtp;

    public static ProfilerStats Empty => new()
    {
        MomentaryLufs = float.NaN, ShortTermLufs = float.NaN, IntegratedLufs = float.NaN, TruePeakDbtp = float.NaN,
    };
}

/// <summary>
/// Where profiler data comes from: the editor audition (<see cref="EditorFeed"/>) or a connected game
/// (<see cref="GameFeed"/>). <see cref="Poll"/> is called once per frame; it refreshes the lists in place
/// and reports whether a new sample arrived (the graph only records new samples).
/// </summary>
public abstract class ProfilerFeed
{
    protected readonly List<ProfilerInstanceRow> InstanceRows = new();
    protected readonly List<ProfilerBusLevel> BusRows = new();

    public ProfilerStats Stats { get; protected set; } = ProfilerStats.Empty;
    public IReadOnlyList<ProfilerInstanceRow> Instances => InstanceRows;
    public IReadOnlyList<ProfilerBusLevel> Buses => BusRows;
    /// <summary>Human-readable connection line for the source bar.</summary>
    public abstract string Status { get; }
    public abstract bool IsLive { get; }
    /// <summary>The system a <c>Recorder</c> can attach to; null for a remote game.</summary>
    public abstract DirgeSystem? RecordableSystem { get; }

    /// <summary>Refresh <see cref="Stats"/> (and bus levels). Called every frame for the graph; allocation-free. True when a new sample arrived.</summary>
    public abstract bool Poll();
    /// <summary>Refresh <see cref="Instances"/> and <see cref="Buses"/> for the tables; called at the document refresh rate.</summary>
    public abstract void PollRows();
    public abstract void StopInstance(int handle, bool immediate);
    public virtual void Dispose() { }
}

/// <summary>Editor audition: reads the live <see cref="DirgeSystem"/> directly every frame.</summary>
public sealed class EditorFeed : ProfilerFeed
{
    private readonly IAudition _audition;
    private readonly ProjectDocument _document;
    private readonly Dictionary<EventInstance, int> _handles = new(ReferenceEqualityComparer.Instance);
    private readonly List<EventInstance> _gone = new();
    private readonly System.Text.StringBuilder _params = new();
    private int _nextHandle = 1;

    public EditorFeed(IAudition audition, ProjectDocument document)
    {
        _audition = audition;
        _document = document;
        _audition.Rebuilt += OnRebuilt;
    }

    public override string Status => _audition.System is null ? "Editor audition · rebuilding" : "Editor audition";
    public override bool IsLive => _audition.System is not null;
    public override DirgeSystem? RecordableSystem => _audition.System;

    private void OnRebuilt()
    {
        _handles.Clear();
    }

    public override bool Poll()
    {
        DirgeSystem? system = _audition.System;
        if (system is null)
        {
            Stats = ProfilerStats.Empty;
            return false;
        }

        ProfilerStats stats = ProfilerStats.Empty;
        stats.Time = system.Time;
        stats.Instances = system.InstanceCount;
        stats.RealVoices = system.RealVoiceCount;
        stats.UpdateMs = _audition.LastUpdateMs;
        Stats = stats;
        return true;
    }

    public override void PollRows()
    {
        DirgeSystem? system = _audition.System;
        InstanceRows.Clear();
        BusRows.Clear();
        if (system is null) return;

        IReadOnlyList<EventInstance> live = system.Instances;
        foreach (KeyValuePair<EventInstance, int> pair in _handles)
        {
            if (pair.Key.State == PlaybackState.Stopped || !Contains(live, pair.Key)) _gone.Add(pair.Key);
        }
        for (int i = 0; i < _gone.Count; i++) _handles.Remove(_gone[i]);
        _gone.Clear();

        for (int i = 0; i < live.Count; i++)
        {
            EventInstance instance = live[i];
            if (!_handles.TryGetValue(instance, out int handle))
            {
                handle = _nextHandle++;
                _handles[instance] = handle;
            }
            InstanceRows.Add(new ProfilerInstanceRow
            {
                Handle = handle,
                Event = instance.Description.Path,
                State = StateName(instance.State),
                Position = instance.TimelinePosition,
                Virtual = instance.IsVirtual,
                Parameters = FormatParameters(instance),
            });
        }
        InstanceRows.Sort(static (a, b) => a.Handle.CompareTo(b.Handle));

        List<Bus> buses = _document.Project.Mixer.Buses;
        for (int i = 0; i < buses.Count; i++)
        {
            (float left, float right) = _audition.BusPeakDb(buses[i].Id);
            BusRows.Add(new ProfilerBusLevel { Bus = buses[i].Name, Left = left, Right = right });
        }
    }

    private static string StateName(PlaybackState state) => state switch
    {
        PlaybackState.Starting => nameof(PlaybackState.Starting),
        PlaybackState.Playing => nameof(PlaybackState.Playing),
        PlaybackState.Sustaining => nameof(PlaybackState.Sustaining),
        PlaybackState.Stopping => nameof(PlaybackState.Stopping),
        _ => nameof(PlaybackState.Stopped),
    };

    private static bool Contains(IReadOnlyList<EventInstance> list, EventInstance instance)
    {
        for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], instance)) return true;
        return false;
    }

    private string FormatParameters(EventInstance instance)
    {
        IReadOnlyList<ParameterDefinition> parameters = instance.Description.Parameters;
        if (parameters.Count == 0) return "";
        _params.Clear();
        for (int i = 0; i < parameters.Count; i++)
        {
            if (i > 0) _params.Append("  ");
            _params.Append(parameters[i].Name).Append('=').Append(instance.GetParameter(parameters[i].Name).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        }
        return _params.ToString();
    }

    public override void StopInstance(int handle, bool immediate)
    {
        foreach (KeyValuePair<EventInstance, int> pair in _handles)
        {
            if (pair.Value == handle)
            {
                pair.Key.Stop(immediate ? StopMode.Immediate : StopMode.AllowFadeout);
                return;
            }
        }
    }

    public override void Dispose()
    {
        _audition.Rebuilt -= OnRebuilt;
    }
}

/// <summary>
/// A game reached through <see cref="LiveUpdateClient"/>; stats arrive at 10 Hz, instances at 5 Hz. The client is either the
/// pane's own (connected here) or the editor shell's shared one, adopted in whatever state it is in — the game accepts a
/// single editor connection, so the profiler must never open a second socket next to the shell's.
/// </summary>
public sealed class GameFeed : ProfilerFeed
{
    private readonly LiveUpdateClient _client;
    private readonly string _endpoint;
    private string _projectName = "";
    private bool _statsDirty;
    private bool _instancesDirty;
    private bool _sawHello;
    private LiveStats? _lastStats;
    private IReadOnlyList<LiveInstance>? _lastInstances;

    /// <param name="connect">False to adopt <paramref name="client"/> as is (already connected or connecting by its owner).</param>
    public GameFeed(LiveUpdateClient client, string host, int port, bool connect = true)
    {
        _client = client;
        _endpoint = host + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _client.Hello += OnHello;
        _client.Stats += OnStats;
        _client.Instances += OnInstances;
        if (connect)
        {
            _client.Connect(host, port);
        }
        else if (_client.IsConnected)
        {
            // The hello already happened; seed what the events would have delivered.
            _sawHello = true;
            _projectName = _client.HelloInfo?.ProjectName ?? "";
            _lastStats = _client.LastStats;
            _statsDirty = _lastStats is not null;
            _lastInstances = _client.LastInstances;
            _instancesDirty = true;
        }
    }

    public override string Status
    {
        get
        {
            if (!_client.IsConnected) return _sawHello ? "Game · disconnected from " + _endpoint : "Game · connecting to " + _endpoint + "…";
            return _projectName.Length > 0 ? "Game · " + _projectName + " @ " + _endpoint : "Game · " + _endpoint;
        }
    }

    public override bool IsLive => _client.IsConnected;
    public override DirgeSystem? RecordableSystem => null;

    private void OnHello(string projectName)
    {
        _sawHello = true;
        _projectName = projectName;
    }

    private void OnStats(LiveStats stats)
    {
        _lastStats = stats;
        _statsDirty = true;
    }

    private void OnInstances(IReadOnlyList<LiveInstance> instances)
    {
        _lastInstances = instances;
        _instancesDirty = true;
    }

    public override bool Poll()
    {
        bool fresh = _statsDirty;
        if (_statsDirty && _lastStats is not null)
        {
            LiveStats s = _lastStats;
            Stats = new ProfilerStats
            {
                Time = s.Time,
                Instances = s.Instances,
                RealVoices = s.RealVoices,
                UpdateMs = s.UpdateMs,
                MomentaryLufs = s.MomentaryLufs,
                ShortTermLufs = s.ShortTermLufs,
                IntegratedLufs = s.IntegratedLufs,
                TruePeakDbtp = s.TruePeakDbtp,
            };
            BusRows.Clear();
            for (int i = 0; i < s.Buses.Count; i++)
            {
                var level = s.Buses[i];
                BusRows.Add(new ProfilerBusLevel { Bus = level.Bus, Left = level.Left, Right = level.Right });
            }
            _statsDirty = false;
        }
        return fresh;
    }

    public override void PollRows()
    {
        if (_instancesDirty && _lastInstances is not null)
        {
            InstanceRows.Clear();
            for (int i = 0; i < _lastInstances.Count; i++)
            {
                LiveInstance li = _lastInstances[i];
                InstanceRows.Add(new ProfilerInstanceRow
                {
                    Handle = li.Handle,
                    Event = li.Event,
                    State = li.State,
                    Position = li.Position,
                    Virtual = li.Virtual,
                    Parameters = "",
                });
            }
            _instancesDirty = false;
        }

        if (!_client.IsConnected && _sawHello)
        {
            InstanceRows.Clear();
        }
    }

    public override void StopInstance(int handle, bool immediate)
    {
        if (_client.IsConnected) _client.SendStop(handle, immediate);
    }

    public override void Dispose()
    {
        _client.Hello -= OnHello;
        _client.Stats -= OnStats;
        _client.Instances -= OnInstances;
        _client.Disconnect();
    }
}
