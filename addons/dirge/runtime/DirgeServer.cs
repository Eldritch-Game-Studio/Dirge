using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Dirge.Project;
using Dirge.Runtime;
using Dirge.Serialization;
using Godot;

namespace Dirge.Driver;

/// <summary>Snapshot of runtime counters for the profiler and live update. Loudness is −∞ while only silence was heard.</summary>
public readonly record struct DirgeStats(
    double Time,
    int Instances,
    int RealVoices,
    double LastUpdateMs,
    int Handles,
    float MomentaryLufs,
    float ShortTermLufs,
    float IntegratedLufs,
    float TruePeakDbtp);

/// <summary>
/// Hosts a <see cref="DirgeSystem"/> inside the scene tree: loads the project, drives <see cref="DirgeSystem.Update"/> from
/// <c>_Process</c>, applies the emitted commands through <see cref="AudioDriver"/> and feeds player feedback back to the model.
/// Also exposes a handle-based surface for GDScript (<see cref="StartEvent"/>, <see cref="SetParameter"/>, ...), bank
/// preloading, Master loudness metering and the live-update host.
/// </summary>
/// <remarks>
/// Command line (after <c>--</c>): <c>--dirge-live[=port]</c> enables live update (default port 7447) regardless of
/// <see cref="LiveUpdatePort"/>; the port is bound when the server loads a project, so an idle autoload never shadows a
/// scene-local server. <c>--dirge-verbose</c> prints driver decisions such as sync-group plays.
/// </remarks>
public partial class DirgeServer : Node
{
    public const string Version = "1.0.0";

    /// <summary>Project setting read by a <see cref="DirgeServer"/> whose <see cref="ProjectPath"/> is empty (the plugin's autoload).</summary>
    public const string ProjectPathSetting = "dirge/project_path";

    /// <summary>Project JSON loaded in <c>_Ready</c>; empty = use <see cref="ProjectPathSetting"/>, or wait for <see cref="Load(AudioProject)"/>.</summary>
    [Export(PropertyHint.File, "*.json")]
    public string ProjectPath { get; set; } = "";

    [Export]
    public int Seed { get; set; }

    /// <summary>Loopback port for the editor's live update; 0 = off. <c>--dirge-live</c> overrides.</summary>
    [Export]
    public int LiveUpdatePort { get; set; }

    /// <summary>Append an <see cref="AudioEffectCapture"/> to Master and measure LUFS/true peak (<see cref="Loudness"/>).</summary>
    [Export]
    public bool MeterLoudness { get; set; } = true;

    private readonly AssetCatalog _assets = new();
    private readonly BankLoader _banks;
    private readonly Dictionary<int, EventInstance> _handles = new();
    private readonly List<int> _finishedHandles = new();
    private readonly Dictionary<string, List<SnapshotInstance>> _snapshots = new(StringComparer.Ordinal);
    private readonly List<string> _finishedSnapshots = new();
    private readonly Stopwatch _updateWatch = new();
    private readonly string _platform = MapPlatform(OS.GetName());
    private AudioDriver? _driver;
    private DirgeSystem? _system;
    private long _lastUpdateTimestamp;
    private LiveUpdateHost? _live;
    private LoudnessCapture? _loudness;
    private int _nextHandle = 1;
    /// <summary>Port requested by <see cref="LiveUpdatePort"/>/<c>--dirge-live</c>, bound by the first <see cref="Load(AudioProject)"/>; 0 = none.</summary>
    private int _pendingLivePort;
    /// <summary>Instance → handle for the signal bridge; one entry per handle in <see cref="_handles"/>.</summary>
    private readonly Dictionary<EventInstance, int> _handleOf = new(ReferenceEqualityComparer.Instance);
    /// <summary>Request being answered while <see cref="SignalName.ProgrammerSound"/> is emitted; <see cref="SetProgrammerSound"/> fills it.</summary>
    private ProgrammerSoundCallback? _programmerRequest;
    private int _programmerRequestHandle;

    /// <summary>Callbacks every handle-based instance forwards as signals; <see cref="SetCallbackMask"/> narrows per handle.</summary>
    public const CallbackMask SignalMask = CallbackMask.Started | CallbackMask.Stopped | CallbackMask.TimelineMarker | CallbackMask.TimelineBeat
        | CallbackMask.SoundPlayed | CallbackMask.Virtualized | CallbackMask.Unvirtualized | CallbackMask.Stolen | CallbackMask.ProgrammerSound;

    [Signal] public delegate void EventStartedEventHandler(int handle);
    [Signal] public delegate void EventStoppedEventHandler(int handle);
    [Signal] public delegate void TimelineMarkerEventHandler(int handle, string name, double position);
    [Signal] public delegate void TimelineBeatEventHandler(int handle, int bar, int beat, float bpm, double position);
    [Signal] public delegate void SoundPlayedEventHandler(int handle, string asset);
    [Signal] public delegate void EventVirtualizedEventHandler(int handle);
    [Signal] public delegate void EventUnvirtualizedEventHandler(int handle);
    [Signal] public delegate void EventStolenEventHandler(int handle);
    /// <summary>A programmer instrument of <c>handle</c> needs an asset for <c>key</c>; answer synchronously with <see cref="SetProgrammerSound"/>.</summary>
    [Signal] public delegate void ProgrammerSoundEventHandler(int handle, string key);

    /// <summary>Raised after <see cref="DirgeSystem.Update"/> and before the commands are applied and cleared; read-only inspection hook.</summary>
    public event Action<IReadOnlyList<Command>>? CommandsEmitted;

    /// <summary>Raised whenever a <see cref="Feedback"/> is handed to the model.</summary>
    public event Action<Feedback>? FeedbackSent;

    /// <summary>Raised after <see cref="Load(AudioProject)"/>/<see cref="HotReload"/> created a new system.</summary>
    public event Action<DirgeSystem>? Loaded;

    public DirgeServer()
    {
        _banks = new BankLoader(_assets);
    }

    public bool IsLoaded => _system is not null;
    public DirgeSystem System => _system ?? throw new InvalidOperationException("Dirge: no project loaded.");
    public AudioDriver Driver => _driver ?? throw new InvalidOperationException("Dirge: driver is created in _Ready.");
    public AssetCatalog Assets => _assets;
    public BankLoader Banks => _banks;
    /// <summary>Live-update host when enabled; null otherwise.</summary>
    public LiveUpdateHost? Live => _live;
    /// <summary>Master loudness meter; null when <see cref="MeterLoudness"/> is off or before <c>_Ready</c>.</summary>
    public LoudnessCapture? Loudness => _loudness;
    /// <summary>Milliseconds the last <see cref="DirgeSystem.Update"/> took (model only, no driver apply).</summary>
    public double LastUpdateMs { get; private set; }
    /// <summary>Platform key used for <c>PlatformOverrides</c>: windows, macos, linux, android, ios, web (from <see cref="OS.GetName"/>).</summary>
    public string Platform => _platform;
    /// <summary>Locale the system resolves audio tables with, e.g. <c>en_US</c> (<see cref="TranslationServer.GetLocale"/>).</summary>
    public string Locale => TranslationServer.GetLocale();
    /// <summary>Instances started through handles (GDScript/live update); keys are handles.</summary>
    public IReadOnlyDictionary<int, EventInstance> Handles => _handles;

    public DirgeStats Stats
    {
        get
        {
            float momentary = float.NegativeInfinity, shortTerm = float.NegativeInfinity, integrated = float.NegativeInfinity, peak = float.NegativeInfinity;
            if (_loudness is not null)
            {
                momentary = _loudness.MomentaryLufs;
                shortTerm = _loudness.ShortTermLufs;
                integrated = _loudness.IntegratedLufs;
                peak = _loudness.TruePeakDbtp;
            }

            return _system is null
                ? new DirgeStats(0, 0, 0, LastUpdateMs, 0, momentary, shortTerm, integrated, peak)
                : new DirgeStats(_system.Time, _system.InstanceCount, _system.RealVoiceCount, LastUpdateMs, _handles.Count, momentary, shortTerm, integrated, peak);
        }
    }

    public override void _Ready()
    {
        _driver = new AudioDriver(this, _assets);
        _driver.VoiceFinished += OnVoiceFinished;
        if (MeterLoudness)
        {
            _loudness = new LoudnessCapture();
            _driver.Capture = _loudness;
        }

        // The live port belongs to the server that owns a project. A server with nothing to load (the plugin's autoload
        // without dirge/project_path) must not take it from a scene-local server; it binds when Load() is first called.
        _pendingLivePort = LiveUpdatePort;
        ParseUserArgs(ref _pendingLivePort);

        string path = ProjectPath;
        if (string.IsNullOrEmpty(path) && ProjectSettings.HasSetting(ProjectPathSetting))
        {
            path = ProjectSettings.GetSetting(ProjectPathSetting).AsString();
        }

        if (!string.IsNullOrEmpty(path))
        {
            LoadFile(path);
        }
    }

    public override void _EnterTree()
    {
        // Re-entering after a removal: _Ready does not run again, so recreate the meter the exit disposed.
        if (_driver is not null && MeterLoudness && _loudness is null)
        {
            _loudness = new LoudnessCapture();
            _driver.Capture = _loudness;
        }
    }

    public override void _ExitTree()
    {
        Unload();
        _driver?.Shutdown();
        _loudness?.Dispose();
        _loudness = null;
    }

    private static void ParseUserArgs(ref int livePort)
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg == "--dirge-verbose")
            {
                AudioDriver.Verbose = true;
            }
            else if (arg == "--dirge-live")
            {
                livePort = LiveProtocol.DefaultPort;
            }
            else if (arg.StartsWith("--dirge-live=", StringComparison.Ordinal))
            {
                string value = arg.Substring("--dirge-live=".Length);
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) && port > 0 && port <= ushort.MaxValue)
                {
                    livePort = port;
                }
                else
                {
                    GD.PushError($"Dirge: invalid --dirge-live port '{value}'; using {LiveProtocol.DefaultPort}.");
                    livePort = LiveProtocol.DefaultPort;
                }
            }
        }
    }

    /// <summary>Starts (or moves) the live-update host on <paramref name="port"/>. Also callable at runtime.</summary>
    public void EnableLiveUpdate(int port)
    {
        _pendingLivePort = 0;
        if (_live is null)
        {
            _live = new LiveUpdateHost(this);
            AddChild(_live);
        }

        _live.Listen(port);
    }

    public void DisableLiveUpdate()
    {
        if (_live is null)
        {
            return;
        }

        _live.Close();
        RemoveChild(_live);
        _live.QueueFree();
        _live = null;
    }

    /// <summary>Stops all instances and voices and drops the system; the bus layout is left as is.</summary>
    public void Unload()
    {
        if (_system is not null)
        {
            _system.StopAll(StopMode.Immediate);
            _system.Update(0);
            Flush();
            _system = null;
        }

        _driver?.StopAll();
        ClearHandles();
        _snapshots.Clear();
    }

    /// <summary>Loads and parses <paramref name="path"/> (a <c>res://</c> or <c>user://</c> JSON file) and calls <see cref="Load(AudioProject)"/>.</summary>
    public void LoadFile(string path)
    {
        if (!FileAccess.FileExists(path))
        {
            GD.PushError($"Dirge: project file '{path}' does not exist.");
            return;
        }

        string json = FileAccess.GetFileAsString(path);
        if (FileAccess.GetOpenError() != Error.Ok)
        {
            GD.PushError($"Dirge: cannot read '{path}': {FileAccess.GetOpenError()}.");
            return;
        }

        Load(ProjectJson.Deserialize(json));
    }

    /// <summary>Replaces the running project: stops everything, rebuilds the engine bus layout and creates a fresh system.</summary>
    public void Load(AudioProject project)
    {
        if (_driver is null)
        {
            throw new InvalidOperationException("Dirge: Load requires the server to be inside the scene tree.");
        }

        Unload();

        _driver.LoadMixer(project.Mixer);
        _system = new DirgeSystem(project, Seed, _assets, platform: _platform,
            elapsedSinceLastUpdate: ElapsedSinceLastUpdate) { Locale = TranslationServer.GetLocale() };
        _lastUpdateTimestamp = 0;
        _loudness?.Reset();
        if (_pendingLivePort > 0)
        {
            EnableLiveUpdate(_pendingLivePort);
        }

        Loaded?.Invoke(_system);
    }

    /// <summary>
    /// Live-update project replace. The model has no in-place edit path, so the system is rebuilt from
    /// <paramref name="project"/>; what survives: global parameter values (by name), running snapshots (by name, restarted),
    /// and every handle whose event still exists (same handle number, restarted at its previous timeline position;
    /// instance parameters, pause state and one-shots already past their end do not survive). Voices restart, so a
    /// short gap is audible on the layers that were playing.
    /// </summary>
    public void HotReload(AudioProject project)
    {
        if (_system is null)
        {
            Load(project);
            return;
        }

        var globals = new List<(string Name, float Value)>(project.GlobalParameters.Count);
        for (int i = 0; i < _system.Project.GlobalParameters.Count; i++)
        {
            string name = _system.Project.GlobalParameters[i].Name;
            globals.Add((name, _system.GetGlobalParameter(name)));
        }

        var running = new List<(int Handle, string Path, double Position)>(_handles.Count);
        foreach (KeyValuePair<int, EventInstance> pair in _handles)
        {
            EventInstance instance = pair.Value;
            if (instance.State is PlaybackState.Stopped or PlaybackState.Stopping)
            {
                continue;
            }

            running.Add((pair.Key, instance.Description.Path, instance.TimelinePosition));
        }

        var snapshots = new List<string>(_snapshots.Count);
        foreach (KeyValuePair<string, List<SnapshotInstance>> pair in _snapshots)
        {
            for (int i = 0; i < pair.Value.Count; i++)
            {
                if (pair.Value[i].IsActive)
                {
                    snapshots.Add(pair.Key);
                }
            }
        }

        int nextHandle = _nextHandle;
        Load(project);
        _nextHandle = nextHandle;

        for (int i = 0; i < globals.Count; i++)
        {
            _system!.SetGlobalParameter(globals[i].Name, globals[i].Value, immediate: true);
        }

        for (int i = 0; i < snapshots.Count; i++)
        {
            try
            {
                StartSnapshot(snapshots[i]);
            }
            catch (KeyNotFoundException)
            {
                // Snapshot removed by the edit; nothing to restore.
            }
        }

        for (int i = 0; i < running.Count; i++)
        {
            (int handle, string path, double position) = running[i];
            if (!_system!.TryGetEvent(path, out EventDescription description))
            {
                continue;
            }

            EventInstance instance = description.CreateInstance();
            Track(handle, instance);
            instance.Start();
            instance.Release();
            if (position > 0 && position < description.Length)
            {
                instance.TimelinePosition = position;
            }
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationPaused || what == NotificationDisabled) _lastUpdateTimestamp = 0;
    }

    private double ElapsedSinceLastUpdate() => _lastUpdateTimestamp == 0 ? 0
        : Stopwatch.GetElapsedTime(_lastUpdateTimestamp).TotalSeconds;

    public override void _Process(double delta)
    {
        _banks.Poll();
        _loudness?.Poll();
        if (_system is null)
        {
            return;
        }

        _updateWatch.Restart();
        // Native streams advance in real time, not the engine's smoothed/scaled frame delta.
        // Startup loading can surface as a large delta on a later frame and otherwise stop
        // a short voice before its first audio mix. Paused/disabled intervals start a fresh clock.
        long now = Stopwatch.GetTimestamp();
        double elapsed = _lastUpdateTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(_lastUpdateTimestamp, now).TotalSeconds;
        _lastUpdateTimestamp = now;
        _system.Update(elapsed);
        _updateWatch.Stop();
        LastUpdateMs = _updateWatch.Elapsed.TotalMilliseconds;
        Flush();
        Sweep();
    }

    private void Flush()
    {
        if (_system is null || _driver is null)
        {
            return;
        }

        CommandsEmitted?.Invoke(_system.Commands.Commands);
        _driver.Apply(_system.Commands);
        _system.Commands.Clear();
    }

    private void OnVoiceFinished(VoiceId voice)
    {
        if (_system is null)
        {
            return;
        }

        var feedback = new VoiceFinished(voice);
        FeedbackSent?.Invoke(feedback);
        _system.Feedback(feedback);
    }

    private void Sweep()
    {
        foreach (KeyValuePair<int, EventInstance> pair in _handles)
        {
            if (pair.Value.State == PlaybackState.Stopped)
            {
                _finishedHandles.Add(pair.Key);
            }
        }

        for (int i = 0; i < _finishedHandles.Count; i++)
        {
            Untrack(_finishedHandles[i]);
        }

        _finishedHandles.Clear();

        foreach (KeyValuePair<string, List<SnapshotInstance>> pair in _snapshots)
        {
            List<SnapshotInstance> list = pair.Value;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (!list[i].IsActive)
                {
                    list.RemoveAt(i);
                }
            }

            if (list.Count == 0)
            {
                _finishedSnapshots.Add(pair.Key);
            }
        }

        for (int i = 0; i < _finishedSnapshots.Count; i++)
        {
            _snapshots.Remove(_finishedSnapshots[i]);
        }

        _finishedSnapshots.Clear();
    }

    private static string MapPlatform(string osName) => osName switch
    {
        "Windows" or "UWP" => "windows",
        "macOS" => "macos",
        "Linux" or "FreeBSD" or "NetBSD" or "OpenBSD" or "BSD" => "linux",
        "Android" => "android",
        "iOS" or "visionOS" => "ios",
        "Web" => "web",
        _ => osName.ToLowerInvariant(),
    };

    // ---- live update hooks ---------------------------------------------------------------------------------------

    /// <summary>
    /// Sets a bus to an absolute authored fader value while the game runs. The model keeps the authored value it was built
    /// with, so this is applied as the runtime override offset (<see cref="MixerState.SetBusVolume"/>) that makes the
    /// evaluated volume equal <paramref name="volumeDb"/> before snapshots/VCAs/automation. False for unknown buses.
    /// </summary>
    public bool LiveSetBusVolume(string bus, float volumeDb)
    {
        if (_system is null)
        {
            return false;
        }

        List<Bus> buses = _system.Project.Mixer.Buses;
        for (int i = 0; i < buses.Count; i++)
        {
            if (buses[i].Name == bus)
            {
                _system.Mixer.SetBusVolume(bus, volumeDb - buses[i].VolumeDb);
                return true;
            }
        }

        return false;
    }

    /// <summary>Writes an effect property straight to the engine (see <see cref="AudioDriver.SetEffectParameter"/>).</summary>
    public bool LiveSetEffectParameter(string bus, int effectIndex, string name, float value)
        => _driver is not null && _driver.SetEffectParameter(bus, effectIndex, name, value);

    // ---- GDScript-friendly surface ---------------------------------------------------------------------------------

    /// <summary>Creates, starts and auto-releases an instance of the event at <paramref name="path"/>. Returns 0 if the event is unknown.</summary>
    public int StartEvent(string path)
    {
        if (_system is null || !_system.TryGetEvent(path, out EventDescription description))
        {
            GD.PushError($"Dirge: unknown event '{path}'.");
            return 0;
        }

        EventInstance instance = description.CreateInstance();
        int handle = _nextHandle++;
        Track(handle, instance);
        instance.Start();
        instance.Release();
        return handle;
    }

    public void StopEvent(int handle, bool immediate = false)
    {
        if (_handles.TryGetValue(handle, out EventInstance? instance))
        {
            instance.Stop(immediate ? StopMode.Immediate : StopMode.AllowFadeout);
        }
    }

    public void StopAllEvents(bool immediate = false) => _system?.StopAll(immediate ? StopMode.Immediate : StopMode.AllowFadeout);

    public void SetParameter(int handle, string name, float value)
    {
        if (_handles.TryGetValue(handle, out EventInstance? instance))
        {
            instance.SetParameter(name, value);
        }
    }

    public float GetParameter(int handle, string name) => _handles.TryGetValue(handle, out EventInstance? instance) ? instance.GetParameter(name) : 0f;

    public void SetGlobalParameter(string name, float value) => _system?.SetGlobalParameter(name, value);

    public float GetGlobalParameter(string name) => _system?.GetGlobalParameter(name) ?? 0f;

    /// <summary>Starts a snapshot by name (several instances may run); throws <see cref="KeyNotFoundException"/> for unknown names.</summary>
    public void StartSnapshot(string name)
    {
        if (_system is null)
        {
            return;
        }

        SnapshotInstance instance = _system.Mixer.StartSnapshot(name);
        if (!_snapshots.TryGetValue(name, out List<SnapshotInstance>? list))
        {
            list = new List<SnapshotInstance>(1);
            _snapshots[name] = list;
        }

        list.Add(instance);
    }

    /// <summary>Releases every running instance of the snapshot started through <see cref="StartSnapshot"/>; the model finishes the AHDSR release.</summary>
    public void StopSnapshot(string name)
    {
        if (_snapshots.Remove(name, out List<SnapshotInstance>? list))
        {
            for (int i = 0; i < list.Count; i++)
            {
                list[i].Stop();
            }
        }
    }

    /// <summary>True between <see cref="StartSnapshot"/> and <see cref="StopSnapshot"/> (a releasing snapshot counts as stopped).</summary>
    public bool IsSnapshotActive(string name) => _snapshots.TryGetValue(name, out List<SnapshotInstance>? list) && list.Count > 0;

    public bool IsEventPlaying(int handle) => _handles.TryGetValue(handle, out EventInstance? instance) && instance.State != PlaybackState.Stopped;

    /// <summary>The instance behind a handle for C# callers; <see langword="null"/> once it has stopped.</summary>
    public EventInstance? GetInstance(int handle) => _handles.TryGetValue(handle, out EventInstance? instance) ? instance : null;

    /// <summary>Restricts which callbacks <paramref name="handle"/> forwards as signals; <paramref name="mask"/> is a <see cref="CallbackMask"/> value, clipped to <see cref="SignalMask"/>.</summary>
    public void SetCallbackMask(int handle, int mask)
    {
        if (_handles.TryGetValue(handle, out EventInstance? instance))
        {
            instance.CallbackMask = (CallbackMask)mask & SignalMask;
        }
    }

    /// <summary>
    /// Supplies the asset for <paramref name="handle"/>'s programmer instruments. Called while <see cref="SignalName.ProgrammerSound"/>
    /// is being emitted it answers that request; otherwise (or additionally) it becomes the instance's fallback asset
    /// (<see cref="EventInstance.SetProgrammerSound"/>).
    /// </summary>
    public void SetProgrammerSound(int handle, string assetPath)
    {
        if (!_handles.TryGetValue(handle, out EventInstance? instance))
        {
            return;
        }

        var asset = new AssetRef(assetPath);
        if (_programmerRequest is not null && _programmerRequestHandle == handle)
        {
            _programmerRequest.Asset = asset;
        }

        instance.SetProgrammerSound(asset);
    }

    private void Track(int handle, EventInstance instance)
    {
        _handles[handle] = instance;
        _handleOf[instance] = handle;
        instance.CallbackMask = SignalMask;
        instance.Callback += OnInstanceCallback;
    }

    private void Untrack(int handle)
    {
        if (_handles.Remove(handle, out EventInstance? instance))
        {
            _handleOf.Remove(instance);
            instance.Callback -= OnInstanceCallback;
        }
    }

    private void ClearHandles()
    {
        foreach (KeyValuePair<int, EventInstance> pair in _handles)
        {
            pair.Value.Callback -= OnInstanceCallback;
        }

        _handles.Clear();
        _handleOf.Clear();
    }

    private void OnInstanceCallback(EventInstance instance, CallbackEvent ev)
    {
        if (!_handleOf.TryGetValue(instance, out int handle))
        {
            return;
        }

        switch (ev)
        {
            case StartedCallback:
                EmitSignalEventStarted(handle);
                break;
            case StoppedCallback:
                EmitSignalEventStopped(handle);
                break;
            case MarkerCallback marker:
                EmitSignalTimelineMarker(handle, marker.Name, marker.Position);
                break;
            case BeatCallback beat:
                EmitSignalTimelineBeat(handle, beat.Bar, beat.Beat, beat.Bpm, beat.Position);
                break;
            case SoundPlayedCallback played:
                EmitSignalSoundPlayed(handle, played.Asset.Path);
                break;
            case VirtualizedCallback:
                EmitSignalEventVirtualized(handle);
                break;
            case UnvirtualizedCallback:
                EmitSignalEventUnvirtualized(handle);
                break;
            case StolenCallback:
                EmitSignalEventStolen(handle);
                break;
            case ProgrammerSoundCallback request:
                // Emission is synchronous: a connected GDScript handler answers through SetProgrammerSound before EmitSignal returns.
                _programmerRequest = request;
                _programmerRequestHandle = handle;
                EmitSignalProgrammerSound(handle, request.Key);
                _programmerRequest = null;
                break;
        }
    }

    /// <summary>Begins loading every asset of the bank in the background; <see cref="IsBankLoaded"/> turns true when done. False for unknown banks.</summary>
    public bool LoadBank(string name) => _system is not null && _banks.Load(_system.Project, name);

    public void UnloadBank(string name) => _banks.Unload(name);

    public bool IsBankLoaded(string name) => _banks.IsLoaded(name);

    public string GetPlatform() => _platform;

    public string GetLocale() => Locale;

    /// <summary>Variant-friendly <see cref="Stats"/> for GDScript: keys time, instances, real_voices, update_ms, handles, momentary_lufs, short_term_lufs, integrated_lufs, true_peak_dbtp.</summary>
    public Godot.Collections.Dictionary GetStats()
    {
        DirgeStats stats = Stats;
        return new Godot.Collections.Dictionary
        {
            ["time"] = stats.Time,
            ["instances"] = stats.Instances,
            ["real_voices"] = stats.RealVoices,
            ["update_ms"] = stats.LastUpdateMs,
            ["handles"] = stats.Handles,
            ["momentary_lufs"] = stats.MomentaryLufs,
            ["short_term_lufs"] = stats.ShortTermLufs,
            ["integrated_lufs"] = stats.IntegratedLufs,
            ["true_peak_dbtp"] = stats.TruePeakDbtp,
        };
    }
}
