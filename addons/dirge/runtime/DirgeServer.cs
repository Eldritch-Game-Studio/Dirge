using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Runtime;
using Dirge.Serialization;
using Godot;

namespace Dirge.Driver;

/// <summary>
/// Hosts a <see cref="DirgeSystem"/> inside the scene tree: loads the project, drives <see cref="DirgeSystem.Update"/> from
/// <c>_Process</c>, applies the emitted commands through <see cref="AudioDriver"/> and feeds player feedback back to the model.
/// Also exposes a handle-based surface for GDScript (<see cref="StartEvent"/>, <see cref="SetParameter"/>, ...).
/// </summary>
public partial class DirgeServer : Node
{
    /// <summary>Project JSON loaded in <c>_Ready</c>; empty = wait for <see cref="Load(AudioProject)"/>.</summary>
    [Export(PropertyHint.File, "*.json")]
    public string ProjectPath { get; set; } = "res://demo/demo.dirge.json";

    [Export]
    public int Seed { get; set; }

    private readonly AssetCatalog _assets = new();
    private readonly Dictionary<int, EventInstance> _handles = new();
    private readonly List<int> _finishedHandles = new();
    private AudioDriver? _driver;
    private DirgeSystem? _system;
    private int _nextHandle = 1;

    /// <summary>Raised after <see cref="DirgeSystem.Update"/> and before the commands are applied and cleared; read-only inspection hook.</summary>
    public event Action<IReadOnlyList<Command>>? CommandsEmitted;

    /// <summary>Raised whenever a <see cref="Feedback"/> is handed to the model.</summary>
    public event Action<Feedback>? FeedbackSent;

    public bool IsLoaded => _system is not null;
    public DirgeSystem System => _system ?? throw new InvalidOperationException("Dirge: no project loaded.");
    public AudioDriver Driver => _driver ?? throw new InvalidOperationException("Dirge: driver is created in _Ready.");
    public AssetCatalog Assets => _assets;

    public override void _Ready()
    {
        _driver = new AudioDriver(this, _assets);
        _driver.VoiceFinished += OnVoiceFinished;
        if (!string.IsNullOrEmpty(ProjectPath))
        {
            LoadFile(ProjectPath);
        }
    }

    public override void _ExitTree()
    {
        Unload();
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
        _handles.Clear();
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
        _system = new DirgeSystem(project, Seed, _assets);
    }

    public override void _Process(double delta)
    {
        if (_system is null)
        {
            return;
        }

        _system.Update(delta);
        Flush();
        SweepHandles();
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

    private void SweepHandles()
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
            _handles.Remove(_finishedHandles[i]);
        }

        _finishedHandles.Clear();
    }

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
        _handles[handle] = instance;
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

    public void SetParameter(int handle, string name, float value)
    {
        if (_handles.TryGetValue(handle, out EventInstance? instance))
        {
            instance.SetParameter(name, value);
        }
    }

    public void SetGlobalParameter(string name, float value) => _system?.SetGlobalParameter(name, value);

    public float GetGlobalParameter(string name) => _system?.GetGlobalParameter(name) ?? 0f;

    public void StartSnapshot(string name) => _system?.Mixer.StartSnapshot(name);

    public bool IsEventPlaying(int handle) => _handles.TryGetValue(handle, out EventInstance? instance) && instance.State != PlaybackState.Stopped;

    /// <summary>The instance behind a handle for C# callers; <see langword="null"/> once it has stopped.</summary>
    public EventInstance? GetInstance(int handle) => _handles.TryGetValue(handle, out EventInstance? instance) ? instance : null;
}
