using System;
using System.Collections.Generic;
using System.Numerics;
using Dirge.Evaluation;
using Dirge.Project;

namespace Dirge.Runtime;

/// <summary>Asset lengths the model may use to schedule one-shot ends without asking the engine.</summary>
public interface IAssetCatalog
{
    bool TryGetLength(AssetRef asset, out double seconds);
}

/// <summary>Default catalog: <see cref="AudioProject.AssetLengths"/>.</summary>
public sealed class ProjectAssetCatalog : IAssetCatalog
{
    private readonly Dictionary<string, double> _lengths;

    public ProjectAssetCatalog(AudioProject project) { _lengths = project.AssetLengths; }

    public bool TryGetLength(AssetRef asset, out double seconds)
    {
        if (!asset.IsEmpty && _lengths.TryGetValue(asset.Path, out seconds) && seconds > 0) return true;
        seconds = 0;
        return false;
    }
}

/// <summary>
/// Runtime root for one <see cref="AudioProject"/>: event descriptions, global parameters, listeners, the voice pool and
/// the mixer. <see cref="Update"/> advances everything and fills <see cref="Commands"/>; the driver applies and clears them.
/// Deterministic for a seed: every random decision goes through one <see cref="DirgeRandom"/>.
/// </summary>
public sealed class DirgeSystem
{
    public const int MaxListeners = 8;

    private readonly Dictionary<string, EventDescription> _byPath = new(StringComparer.Ordinal);
    private readonly Dictionary<DirgeId, EventDescription> _byId = new();
    private readonly List<EventDescription> _descriptions = new();
    private readonly List<EventInstance> _instances = new();
    private readonly List<EventInstance> _destroyQueue = new();
    private readonly List<Feedback> _feedback = new();
    private readonly Attributes3D[] _listeners = new Attributes3D[MaxListeners];
    private readonly float[] _listenerWeights = new float[MaxListeners];
    private int _totalInstances;

    public AudioProject Project { get; }
    public MixerState Mixer { get; }
    public ParameterBank GlobalParameters { get; }
    /// <summary>Filled by <see cref="Update"/>; the driver calls <see cref="CommandBuffer.Clear"/> after applying.</summary>
    public CommandBuffer Commands { get; } = new();
    /// <summary>Seconds, sum of every <c>dt</c> passed to <see cref="Update"/>.</summary>
    public double Time { get; private set; }
    /// <summary>Live instances including nested children.</summary>
    public int InstanceCount => _totalInstances;
    public int RealVoiceCount => Voices.RealCount;
    /// <summary>Global cap on voices with an engine player; the rest virtualize by Priority.</summary>
    public int MaxRealVoices { get; set; } = 64;

    internal DirgeRandom Random { get; }
    internal IAssetCatalog Assets { get; }
    internal VoicePool Voices { get; }

    public DirgeSystem(AudioProject project, int seed = 0, IAssetCatalog? assets = null)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        Random = new DirgeRandom(seed);
        Assets = assets ?? new ProjectAssetCatalog(project);
        GlobalParameters = new ParameterBank(project.GlobalParameters);
        Voices = new VoicePool(project.Mixer);
        for (int i = 0; i < MaxListeners; i++) _listeners[i] = Attributes3D.Origin;
        _listenerWeights[0] = 1f;

        for (int i = 0; i < project.Events.Count; i++)
        {
            var description = new EventDescription(this, project.Events[i], project, Assets);
            _descriptions.Add(description);
            _byId[description.Id] = description;
            _byPath[description.Path] = description;
        }
        for (int i = 0; i < _descriptions.Count; i++) _descriptions[i].Link();

        Mixer = new MixerState(project, GlobalParameters, Commands, Random);
    }

    // ---- lookup --------------------------------------------------------------------------------------------------

    public bool TryGetEvent(string path, out EventDescription description) => _byPath.TryGetValue(path, out description!);

    internal bool TryGetEvent(DirgeId id, out EventDescription description) => _byId.TryGetValue(id, out description!);

    public EventDescription GetEvent(string path)
        => _byPath.TryGetValue(path, out EventDescription? d) ? d : throw new KeyNotFoundException($"Event '{path}' not found.");

    public EventDescription GetEvent(DirgeId id)
        => _byId.TryGetValue(id, out EventDescription? d) ? d : throw new KeyNotFoundException($"Event {id} not found.");

    // ---- parameters / listeners ----------------------------------------------------------------------------------

    public void SetGlobalParameter(string name, float value, bool immediate = false)
    {
        if (GlobalParameters.TryGet(name, out ParameterState state)) state.Set(value, immediate);
    }

    public float GetGlobalParameter(string name) => GlobalParameters.TryGet(name, out ParameterState state) ? state.Value : 0f;

    /// <summary>Listener <paramref name="index"/> in 0..7. Weight 0 disables it. Built-ins use the nearest enabled listener.</summary>
    public void SetListener(int index, Attributes3D attributes, float weight = 1f)
    {
        if (index < 0 || index >= MaxListeners) throw new ArgumentOutOfRangeException(nameof(index));
        _listeners[index] = attributes;
        _listenerWeights[index] = MathF.Max(0f, weight);
    }

    internal Attributes3D NearestListener(Vector3 position)
    {
        int best = -1;
        float bestDistance = float.PositiveInfinity;
        for (int i = 0; i < MaxListeners; i++)
        {
            if (_listenerWeights[i] <= 0f) continue;
            float d = Vector3.DistanceSquared(_listeners[i].Position, position);
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        return best < 0 ? _listeners[0] : _listeners[best];
    }

    // ---- lifecycle -----------------------------------------------------------------------------------------------

    public void StopAll(StopMode mode)
    {
        for (int i = _instances.Count - 1; i >= 0; i--)
            if (i < _instances.Count) _instances[i].Stop(mode);
    }

    /// <summary>Driver → model. Applied at the start of the next <see cref="Update"/>.</summary>
    public void Feedback(Feedback feedback)
    {
        if (feedback is not null) _feedback.Add(feedback);
    }

    internal void OnInstanceCreated(EventInstance instance)
    {
        _totalInstances++;
        if (instance.Parent is null) _instances.Add(instance);
    }

    /// <summary>Called by nested instances' parents and by top-level destroy; keeps counts right.</summary>
    internal void OnInstanceDestroyed()
    {
        _totalInstances--;
    }

    internal void RequestDestroy(EventInstance instance)
    {
        if (!_destroyQueue.Contains(instance)) _destroyQueue.Add(instance);
    }

    private void FlushDestroyQueue()
    {
        for (int i = 0; i < _destroyQueue.Count; i++)
        {
            EventInstance instance = _destroyQueue[i];
            if (instance.IsDestroyed) continue;
            int index = _instances.IndexOf(instance);
            if (index >= 0) _instances.RemoveAt(index);
            instance.Destroy();
        }
        _destroyQueue.Clear();
    }

    /// <summary>Command instrument actions, executed in the context of the triggering instance.</summary>
    internal void ExecuteCommand(CommandInstrument command, EventInstance source)
    {
        switch (command.Action)
        {
            case CommandAction.StartEvent:
                if (TryGetEvent(command.TargetEvent, out EventDescription target))
                {
                    EventInstance instance = target.CreateInstance();
                    instance.Start();
                    instance.Release();
                }
                break;
            case CommandAction.StopEvent:
                if (TryGetEvent(command.TargetEvent, out target)) target.StopAll(StopMode.AllowFadeout);
                break;
            case CommandAction.SetParameter:
                source.ResolveParameter(command.TargetParameter)?.Set(command.Value);
                break;
        }
    }

    // ---- tick ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Order: feedback → global parameters → instances (timeline, instruments, voices) → real-voice limits →
    /// voice command emission → <see cref="MixerState.Update"/>.
    /// </summary>
    public void Update(double dt)
    {
        if (dt < 0) dt = 0;
        FlushDestroyQueue();

        for (int i = 0; i < _feedback.Count; i++)
        {
            Voice? voice;
            switch (_feedback[i])
            {
                case VoiceFinished finished:
                    voice = Voices.Find(finished.Voice);
                    // When the catalog gave us the length we scheduled the end ourselves; a late report is ignored.
                    if (voice is not null && !voice.LengthKnown) voice.Owner.FinishVoice(voice);
                    break;
                case VoiceFailed failed:
                    voice = Voices.Find(failed.Voice);
                    if (voice is not null) voice.Owner.FinishVoice(voice);
                    break;
            }
        }
        _feedback.Clear();

        GlobalParameters.Advance(dt);

        int count = _instances.Count;
        for (int i = 0; i < count && i < _instances.Count; i++) _instances[i].UpdateInternal(dt);

        for (int i = 0; i < _descriptions.Count; i++) _descriptions[i].ApplyVirtualizeStealing();
        Voices.ApplyLimits(Math.Max(0, MaxRealVoices), Commands);

        count = _instances.Count;
        for (int i = 0; i < count && i < _instances.Count; i++) _instances[i].EmitInternal(Commands);

        Mixer.Update(dt);
        Time += dt;
        FlushDestroyQueue();
    }
}
