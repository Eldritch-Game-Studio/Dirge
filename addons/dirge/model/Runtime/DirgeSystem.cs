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

    /// <summary>One programmer key's locale map and its fallback (first authored locale).</summary>
    private readonly struct TableEntry
    {
        public readonly Dictionary<string, AssetRef> Locales;
        public readonly AssetRef Fallback;
        public TableEntry(Dictionary<string, AssetRef> locales, AssetRef fallback) { Locales = locales; Fallback = fallback; }
    }

    private readonly Dictionary<string, EventDescription> _byPath = new(StringComparer.Ordinal);
    private readonly Dictionary<DirgeId, EventDescription> _byId = new();
    private readonly List<EventDescription> _descriptions = new();
    private readonly List<EventInstance> _instances = new();
    private readonly List<EventInstance> _destroyQueue = new();
    private readonly List<Feedback> _feedback = new();
    private readonly Attributes3D[] _listeners = new Attributes3D[MaxListeners];
    private readonly float[] _listenerWeights = new float[MaxListeners];
    private readonly Dictionary<string, TableEntry> _tables = new(StringComparer.Ordinal);
    private int _totalInstances;
    private int _maxRealVoices = 64;
    private string _locale = "";
    private readonly Func<double>? _elapsedSinceLastUpdate;
    private bool _updating;

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
    /// <summary>Top-level live instances (created through the API or by command instruments); nested children are not listed.</summary>
    public IReadOnlyList<EventInstance> Instances => _instances;
    /// <summary>Seed the system was built with; a replay with the same project, seed and API calls emits identical commands.</summary>
    public int Seed { get; }
    /// <summary>Platform string the mixer baseline was resolved for (<see cref="AudioProject.PlatformOverrides"/>), or null.</summary>
    public string? Platform { get; }

    /// <summary>Global cap on voices with an engine player; the rest virtualize by Priority.</summary>
    public int MaxRealVoices
    {
        get => _maxRealVoices;
        set
        {
            Recorder?.Call(null, "SetMaxRealVoices", null, null, value, false, 0);
            _maxRealVoices = value;
        }
    }

    /// <summary>Locale code used to resolve programmer keys through <see cref="AudioProject.AudioTables"/>; "" = first authored locale.</summary>
    public string Locale
    {
        get => _locale;
        set
        {
            Recorder?.Call(null, "SetLocale", value, null, 0, false, 0);
            _locale = value ?? "";
        }
    }

    /// <summary>
    /// Playing instance of the most recently started event flagged <see cref="AudioEvent.IsConductor"/>; null when none plays.
    /// Quantized starts (<see cref="EventMacros.StartQuantization"/>) wait for its grid.
    /// </summary>
    public EventInstance? Conductor { get; private set; }

    /// <summary>Optional observer of API calls and ticks (profiler recording). Null in production: every hook is a null check.</summary>
    public IApiSink? Recorder { get; set; }

    internal DirgeRandom Random { get; }
    internal IAssetCatalog Assets { get; }
    internal VoicePool Voices { get; }

    // Mutations inside Update already occur on the model's tick, not between driver ticks.
    internal double? MutationUpdateOffset => _updating ? null : _elapsedSinceLastUpdate?.Invoke();

    /// <param name="elapsedSinceLastUpdate">Optional driver clock: real seconds since the start of its last Update,
    /// or zero before its first tick/after resetting its clock. Starts and seeks exclude that preceding interval.
    /// Omit for deterministic callers whose mutations occur at tick boundaries.</param>
    public DirgeSystem(AudioProject project, int seed = 0, IAssetCatalog? assets = null, string? platform = null,
        Func<double>? elapsedSinceLastUpdate = null)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        Seed = seed;
        Platform = platform;
        _elapsedSinceLastUpdate = elapsedSinceLastUpdate;
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

        // Earlier tables win for a key defined twice; within a key the first authored locale is the fallback.
        for (int t = 0; t < project.AudioTables.Count; t++)
        {
            foreach (KeyValuePair<string, Dictionary<string, AssetRef>> entry in project.AudioTables[t].Entries)
            {
                if (_tables.ContainsKey(entry.Key)) continue;
                AssetRef fallback = default;
                foreach (KeyValuePair<string, AssetRef> locale in entry.Value) { fallback = locale.Value; break; }
                _tables[entry.Key] = new TableEntry(entry.Value, fallback);
            }
        }

        Mixer = new MixerState(project, GlobalParameters, Commands, Random, platform) { System = this };
    }

    /// <summary>Audio-table lookup: <see cref="Locale"/> first, then the key's first authored locale. Empty when the key is unknown.</summary>
    internal AssetRef ResolveTableAsset(string key)
    {
        if (!_tables.TryGetValue(key, out TableEntry entry)) return default;
        return _locale.Length > 0 && entry.Locales.TryGetValue(_locale, out AssetRef asset) ? asset : entry.Fallback;
    }

    internal void OnConductorPlaying(EventInstance instance) => Conductor = instance;

    internal void OnConductorStopped(EventInstance instance)
    {
        if (!ReferenceEquals(Conductor, instance)) return;
        Conductor = null;
        // Fall back to the most recently started conductor instance that is still playing.
        double best = double.NegativeInfinity;
        for (int i = 0; i < _instances.Count; i++)
        {
            EventInstance candidate = _instances[i];
            if (!candidate.Description.Event.IsConductor || !candidate.CanTrigger || candidate.StartTime < best) continue;
            best = candidate.StartTime;
            Conductor = candidate;
        }
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
        Recorder?.Call(null, "SetGlobalParameter", name, null, value, immediate, 0);
        if (GlobalParameters.TryGet(name, out ParameterState state)) state.Set(value, immediate);
    }

    public float GetGlobalParameter(string name) => GlobalParameters.TryGet(name, out ParameterState state) ? state.Value : 0f;

    /// <summary>Listener index 0..7; weight 0 disables. Built-ins select the nearest enabled listener in the emitter's region, not a weighted mix.</summary>
    public void SetListener(int index, Attributes3D attributes, float weight = 1f)
    {
        if (index < 0 || index >= MaxListeners) throw new ArgumentOutOfRangeException(nameof(index));
        Recorder?.Call3D(null, "SetListener", index, attributes, weight);
        _listeners[index] = attributes;
        _listenerWeights[index] = MathF.Max(0f, weight);
    }

    internal bool TryNearestListener(in Attributes3D emitter, out Attributes3D listener)
    {
        int best = -1;
        float bestDistance = float.PositiveInfinity;
        for (int i = 0; i < MaxListeners; i++)
        {
            if (_listenerWeights[i] <= 0f || _listeners[i].Region != emitter.Region) continue;
            float d = Vector3.DistanceSquared(_listeners[i].Position, emitter.Position);
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        // Preserve the legacy all-disabled fallback, but never borrow a different region's listener.
        if (best < 0 && emitter.Region == default && _listeners[0].Region == default) best = 0;
        listener = best < 0 ? default : _listeners[best];
        return best >= 0;
    }

    // ---- lifecycle -----------------------------------------------------------------------------------------------

    public void StopAll(StopMode mode)
    {
        IApiSink? recorder = Recorder;
        recorder?.Call(null, "StopAll", null, null, 0, false, (int)mode);
        recorder?.EnterInternal();
        for (int i = _instances.Count - 1; i >= 0; i--)
            if (i < _instances.Count) _instances[i].Stop(mode);
        recorder?.ExitInternal();
    }

    /// <summary>Driver → model. Applied at the start of the next <see cref="Update"/>.</summary>
    public void Feedback(Feedback feedback)
    {
        if (feedback is null) return;
        if (Recorder is not null) RecordFeedback(feedback);
        _feedback.Add(feedback);
    }

    private void RecordFeedback(Feedback feedback)
    {
        switch (feedback)
        {
            case VoiceFinished finished:
                Recorder!.Call(null, "Feedback", "VoiceFinished", null, finished.Voice.Value, false, 0);
                break;
            case VoiceFailed failed:
                Recorder!.Call(null, "Feedback", "VoiceFailed", failed.Reason, failed.Voice.Value, false, 0);
                break;
        }
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
        _updating = true;
        try { UpdateInternal(dt); }
        finally { _updating = false; }
    }

    private void UpdateInternal(double dt)
    {
        if (dt < 0) dt = 0;
        IApiSink? recorder = Recorder;
        recorder?.BeginUpdate(this, dt);
        recorder?.EnterInternal();
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
        Voices.ApplyLimits(Math.Max(0, _maxRealVoices), Commands);

        count = _instances.Count;
        for (int i = 0; i < count && i < _instances.Count; i++) _instances[i].EmitInternal(Commands);

        Mixer.Update(dt);
        Time += dt;
        FlushDestroyQueue();
        recorder?.ExitInternal();
        recorder?.EndUpdate(this);
    }
}
