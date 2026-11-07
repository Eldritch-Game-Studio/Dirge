using System;
using System.Collections.Generic;
using System.Numerics;
using Dirge.Evaluation;
using Dirge.Project;

namespace Dirge.Runtime;

internal enum InstrumentKind { Clip, Multi, Scatterer, Silence, Event, Programmer, Command, Snapshot }

internal enum ModKind { Random, Ahdsr, Lfo, Autopitch }

/// <summary>A modulator resolved to its kind and whether it drives volume (dB) or pitch (ratio).</summary>
internal readonly struct ModInfo
{
    public readonly Modulator Modulator;
    public readonly ModKind Kind;
    public readonly bool IsVolume;

    public ModInfo(Modulator modulator, ModKind kind, bool isVolume)
    {
        Modulator = modulator;
        Kind = kind;
        IsVolume = isVolume;
    }
}

/// <summary>Per-instance (or per-trigger) state of one modulator.</summary>
internal struct ModRuntime
{
    public float Random;
    public AhdsrState Ahdsr;
    public LfoState Lfo;
    /// <summary>Autopitch: the driving parameter resolved against the owning instance (local, then global); null = unmodulated.</summary>
    public ParameterState? Parameter;
}

/// <summary>
/// Modulator and automation-lane evaluation shared by events and instruments.
/// Volume contributions are dB added to the chain; pitch contributions are ratios multiplied into it.
/// Random: ±Amount. LFO: Amount × waveform. AHDSR: Amount × (level − 1), i.e. <c>Amount</c> is the depth below the
/// authored value at level 0 so a release fades towards silence (80 dB) rather than towards full volume.
/// Autopitch (pitch only): Amount × (<see cref="AutopitchMath.Ratio"/> − 1).
/// </summary>
internal static class ModEval
{
    private const float MinPitch = 0.01f;

    public static ModInfo[] Collect(List<Modulator> source, PropertyKind volumeKind, PropertyKind pitchKind, DirgeId owner)
    {
        var list = new List<ModInfo>();
        for (int i = 0; i < source.Count; i++)
        {
            Modulator m = source[i];
            PropertyRef t = m.Target;
            if (t.Kind != volumeKind && t.Kind != pitchKind) continue;
            if (!t.Owner.IsNone && t.Owner != owner) continue;
            ModKind kind;
            switch (m)
            {
                case RandomModulator: kind = ModKind.Random; break;
                case AhdsrModulator: kind = ModKind.Ahdsr; break;
                case LfoModulator: kind = ModKind.Lfo; break;
                case AutopitchModulator when t.Kind == pitchKind: kind = ModKind.Autopitch; break;
                default: continue;
            }
            list.Add(new ModInfo(m, kind, t.Kind == volumeKind));
        }
        return list.ToArray();
    }

    public static AutomationLane[] CollectLanes(List<AutomationLane> source, PropertyKind kind, DirgeId owner)
    {
        var list = new List<AutomationLane>();
        for (int i = 0; i < source.Count; i++)
        {
            AutomationLane lane = source[i];
            if (lane.Target.Kind != kind) continue;
            if (!lane.Target.Owner.IsNone && lane.Target.Owner != owner) continue;
            list.Add(lane);
        }
        return list.ToArray();
    }

    /// <summary>Resolve autopitch parameters once per instance; the binding survives pooled reuse of the instance.</summary>
    public static void BindParameters(ModInfo[] infos, ModRuntime[] runtime, EventInstance instance)
    {
        for (int i = 0; i < infos.Length; i++)
            if (infos[i].Kind == ModKind.Autopitch)
                runtime[i].Parameter = instance.ResolveParameter(((AutopitchModulator)infos[i].Modulator).Parameter);
    }

    public static void Init(ModInfo[] infos, ModRuntime[] runtime, DirgeRandom random)
    {
        for (int i = 0; i < infos.Length; i++)
        {
            switch (infos[i].Kind)
            {
                case ModKind.Random:
                    runtime[i].Random = random.NextSigned();
                    break;
                case ModKind.Ahdsr:
                    runtime[i].Ahdsr = new AhdsrState(((AhdsrModulator)infos[i].Modulator).Envelope);
                    break;
                case ModKind.Lfo:
                    runtime[i].Lfo = new LfoState((LfoModulator)infos[i].Modulator, random);
                    break;
            }
        }
    }

    /// <summary>Advance every modulator by <paramref name="dt"/> (0 = sample only) and sum/multiply their contributions.</summary>
    public static void Advance(ModInfo[] infos, ModRuntime[] runtime, double dt, float bpm, out float volumeDb, out float pitch)
    {
        volumeDb = 0f;
        pitch = 1f;
        for (int i = 0; i < infos.Length; i++)
        {
            ref readonly ModInfo info = ref infos[i];
            float amount = info.Modulator.Amount;
            float contribution;
            switch (info.Kind)
            {
                case ModKind.Random:
                    contribution = amount * runtime[i].Random;
                    break;
                case ModKind.Ahdsr:
                {
                    float level = dt > 0 ? runtime[i].Ahdsr.Advance(dt) : runtime[i].Ahdsr.Current;
                    contribution = amount * (level - 1f);
                    break;
                }
                case ModKind.Lfo:
                {
                    var lfo = (LfoModulator)info.Modulator;
                    float rate = lfo.RateHz;
                    if (lfo.TempoSync && bpm > 0f) rate = rate <= 0f ? 0f : bpm / 60f / rate;
                    float wave = dt > 0 ? runtime[i].Lfo.Advance(dt, rate) : runtime[i].Lfo.Current;
                    contribution = amount * wave;
                    break;
                }
                case ModKind.Autopitch:
                {
                    ParameterState? p = runtime[i].Parameter;
                    if (p is null) { contribution = 0f; break; }
                    ParameterDefinition d = p.Definition;
                    contribution = amount * (AutopitchMath.Ratio((AutopitchModulator)info.Modulator, d.Minimum, d.Maximum, p.Value) - 1f);
                    break;
                }
                default:
                    contribution = 0f;
                    break;
            }
            if (info.IsVolume) volumeDb += contribution;
            else pitch *= MathF.Max(MinPitch, 1f + contribution);
        }
    }

    public static void Release(ModInfo[] infos, ModRuntime[] runtime)
    {
        for (int i = 0; i < infos.Length; i++)
            if (infos[i].Kind == ModKind.Ahdsr) runtime[i].Ahdsr.Release();
    }

    /// <summary>Longest release among volume AHDSR modulators; a fade-out must last at least this long to be audible.</summary>
    public static float MaxVolumeRelease(ModInfo[] infos)
    {
        float max = 0f;
        for (int i = 0; i < infos.Length; i++)
            if (infos[i].Kind == ModKind.Ahdsr && infos[i].IsVolume)
                max = MathF.Max(max, ((AhdsrModulator)infos[i].Modulator).Envelope.Release);
        return max;
    }

    public static float SumLanes(AutomationLane[] lanes, ParameterState?[] states)
    {
        float sum = 0f;
        for (int i = 0; i < lanes.Length; i++)
        {
            ParameterState? s = states[i];
            if (s is not null) sum += CurveMath.Evaluate(lanes[i].Curve, s.Value);
        }
        return sum;
    }

    public static float ProductLanes(AutomationLane[] lanes, ParameterState?[] states)
    {
        float product = 1f;
        for (int i = 0; i < lanes.Length; i++)
        {
            ParameterState? s = states[i];
            if (s is not null) product *= MathF.Max(MinPitch, CurveMath.Evaluate(lanes[i].Curve, s.Value));
        }
        return product;
    }
}

/// <summary>Immutable per-description view of one instrument: kind, region, resolved lanes and modulators.</summary>
internal sealed class InstrumentInfo
{
    /// <summary>Track stand-in for transition-timeline instruments, which belong to a marker rather than a track.</summary>
    public static readonly Track NoTrack = new() { Name = "(transition)" };

    public readonly Instrument Instrument;
    public readonly Track Track;
    public readonly InstrumentKind Kind;
    public readonly bool OnTimeline;
    /// <summary>Timeline: seconds. Sheet: parameter range start.</summary>
    public readonly double RegionStart;
    /// <summary>Timeline region end (exclusive); +∞ when the natural length is unknown. Sheet: range end.</summary>
    public readonly double RegionEnd;
    /// <summary>Zero-length timeline trigger (command instruments): fires when the playhead crosses <see cref="RegionStart"/>.</summary>
    public readonly bool IsPoint;
    /// <summary>
    /// Non-null for a transition-timeline instrument: the <see cref="TransitionMarker"/>/<see cref="TransitionRegion"/> it belongs
    /// to. Its region is measured on that transition's local clock, never on the event timeline or a parameter sheet.
    /// </summary>
    public readonly LogicMarker? Transition;
    public readonly AutomationLane[] VolumeLanes;
    public readonly AutomationLane[] PitchLanes;
    public readonly ModInfo[] Modulators;
    public readonly float MaxVolumeRelease;
    public readonly int EntryCount;
    public readonly ClipInstrument? Clip;
    public readonly MultiInstrument? Multi;
    public readonly ScattererInstrument? Scatterer;
    public readonly ProgrammerInstrument? Programmer;
    public readonly CommandInstrument? Command;
    public readonly EventInstrument? EventInstrument;
    public readonly string? SnapshotName;
    /// <summary>Resolved by <see cref="EventDescription.Link"/> once every description exists.</summary>
    public EventDescription? NestedEvent;

    public InstrumentInfo(AudioEvent ev, Track track, Instrument instrument, AudioProject project, IAssetCatalog assets, LogicMarker? transition)
    {
        Instrument = instrument;
        Track = track;
        Transition = transition;
        VolumeLanes = ModEval.CollectLanes(ev.Automation, PropertyKind.InstrumentVolumeDb, instrument.Id);
        PitchLanes = ModEval.CollectLanes(ev.Automation, PropertyKind.InstrumentPitch, instrument.Id);
        Modulators = ModEval.Collect(instrument.Modulators, PropertyKind.InstrumentVolumeDb, PropertyKind.InstrumentPitch, instrument.Id);
        MaxVolumeRelease = ModEval.MaxVolumeRelease(Modulators);

        double natural = 0;
        switch (instrument)
        {
            case ClipInstrument c:
                Kind = InstrumentKind.Clip;
                Clip = c;
                if (assets.TryGetLength(c.Asset, out double len)) natural = len;
                break;
            case MultiInstrument m:
                Kind = InstrumentKind.Multi;
                Multi = m;
                EntryCount = m.Entries.Count;
                natural = MaxEntryLength(m.Entries, assets);
                break;
            case ScattererInstrument s:
                Kind = InstrumentKind.Scatterer;
                Scatterer = s;
                EntryCount = s.Entries.Count;
                break;
            case SilenceInstrument:
                Kind = InstrumentKind.Silence;
                break;
            case EventInstrument e:
                Kind = InstrumentKind.Event;
                EventInstrument = e;
                break;
            case ProgrammerInstrument p:
                Kind = InstrumentKind.Programmer;
                Programmer = p;
                break;
            case CommandInstrument cmd:
                Kind = InstrumentKind.Command;
                Command = cmd;
                break;
            case SnapshotInstrument snap:
                Kind = InstrumentKind.Snapshot;
                for (int i = 0; i < project.Snapshots.Count; i++)
                    if (project.Snapshots[i].Id == snap.Snapshot) { SnapshotName = project.Snapshots[i].Name; break; }
                break;
            default:
                Kind = InstrumentKind.Silence;
                break;
        }

        Placement placement = instrument.Placement;
        // Transition instruments always live on the transition clock, whatever their placement says (the validator flags sheets).
        OnTimeline = transition is not null || placement.OnTimeline;
        RegionStart = placement.Start;
        if (OnTimeline)
        {
            IsPoint = Kind == InstrumentKind.Command || (Kind == InstrumentKind.Silence && placement.Length <= 0f);
            if (IsPoint) RegionEnd = RegionStart;
            else if (placement.Length > 0f) RegionEnd = RegionStart + placement.Length;
            else if (natural > 0 && !instrument.Loop) RegionEnd = RegionStart + natural;
            else RegionEnd = double.PositiveInfinity;
        }
        else
        {
            RegionEnd = RegionStart + Math.Max(0f, placement.Length);
        }
    }

    /// <summary>End used for the event's <see cref="EventDescription.Length"/>: finite region end, else the start.</summary>
    public double LengthContribution => double.IsPositiveInfinity(RegionEnd) ? RegionStart : RegionEnd;

    private static double MaxEntryLength(List<MultiEntry> entries, IAssetCatalog assets)
    {
        double max = 0;
        for (int i = 0; i < entries.Count; i++)
            if (assets.TryGetLength(entries[i].Asset, out double len)) max = Math.Max(max, len);
        return max;
    }
}

/// <summary>
/// Per-instance runtime of one instrument: trigger edges, delay, playlist state, scatterer timer, owned voices,
/// nested child instances, snapshot handle and the instrument half of the gain/pitch chain.
/// </summary>
internal sealed class InstrumentState
{
    public readonly InstrumentInfo Info;
    private readonly EventInstance _instance;
    private readonly DirgeSystem _system;
    private readonly ModRuntime[] _mods;
    private readonly ParameterState?[] _volumeLaneParams;
    private readonly ParameterState?[] _pitchLaneParams;
    /// <summary>Parameter driving a sheet instrument; null on the timeline or when unresolved.</summary>
    public readonly ParameterState? SheetParameter;

    public bool Active;
    private double _delayRemaining = -1;
    private double _pendingOffset;

    public Voice? Head;
    public int VoiceCount;
    public readonly List<EventInstance> Children = new();
    private SnapshotInstance? _snapshot;

    private int[] _recent;
    private int _recentCount;
    private int _recentHead;
    private int[] _shuffle;
    private int _shuffleIndex;
    private int _sequential;
    private int _lastPick = -1;

    private bool _scattering;
    private double _scatterTimer;

    /// <summary>Instrument + track + instrument automation + instrument modulators, this tick.</summary>
    public float ChainVolumeDb;
    public float ChainPitch = 1f;

    public InstrumentState(InstrumentInfo info, EventInstance instance, DirgeSystem system)
    {
        Info = info;
        _instance = instance;
        _system = system;
        _mods = new ModRuntime[info.Modulators.Length];
        ModEval.BindParameters(info.Modulators, _mods, instance);
        _volumeLaneParams = new ParameterState?[info.VolumeLanes.Length];
        _pitchLaneParams = new ParameterState?[info.PitchLanes.Length];
        for (int i = 0; i < info.VolumeLanes.Length; i++) _volumeLaneParams[i] = instance.ResolveParameter(info.VolumeLanes[i].Parameter);
        for (int i = 0; i < info.PitchLanes.Length; i++) _pitchLaneParams[i] = instance.ResolveParameter(info.PitchLanes[i].Parameter);
        SheetParameter = info.OnTimeline ? null : instance.ResolveParameter(info.Instrument.Placement.Parameter);
        _recent = new int[Math.Max(1, info.EntryCount)];
        _shuffle = new int[Math.Max(1, info.EntryCount)];
        _shuffleIndex = int.MaxValue;
    }

    /// <summary>Back to the just-created state. Voices, children and snapshots must already be gone.</summary>
    public void Reset()
    {
        Active = false;
        _delayRemaining = -1;
        _pendingOffset = 0;
        Head = null;
        VoiceCount = 0;
        Children.Clear();
        _snapshot = null;
        _recentCount = 0;
        _recentHead = 0;
        _shuffleIndex = int.MaxValue;
        _sequential = 0;
        _lastPick = -1;
        _scattering = false;
        _scatterTimer = 0;
        ChainVolumeDb = 0f;
        ChainPitch = 1f;
    }

    /// <summary>Anything that keeps the owning instance alive: voices, children, a pending delay or a spawning scatterer.</summary>
    public bool IsBusy => VoiceCount > 0 || Children.Count > 0 || _delayRemaining >= 0 || _scattering;

    public bool HasActiveSnapshot => _snapshot is not null && _snapshot.IsActive;

    // ---- trigger edges -------------------------------------------------------------------------------------------

    /// <summary>Playhead/parameter entered the region; <paramref name="offset"/> = seconds past the region start.</summary>
    public void OnEnter(double offset)
    {
        if (!_instance.CanTrigger) return;
        Active = true;
        Instrument inst = Info.Instrument;
        if (!_instance.ConditionsPass(inst.Conditions)) return;
        if (!_system.Random.Chance(inst.Probability)) return;
        if (inst.TriggerDelay > 0f)
        {
            _delayRemaining = inst.TriggerDelay;
            _pendingOffset = offset;
        }
        else
        {
            Fire(offset);
        }
    }

    /// <summary>Zero-length timeline trigger crossed.</summary>
    public void OnPoint()
    {
        OnEnter(0);
        Active = false;
    }

    public void OnExit()
    {
        Active = false;
        if (Info.Instrument.Async) return;
        _delayRemaining = -1;
        Cut(Info.Instrument.CutMode == CutMode.Cut ? StopMode.Immediate : StopMode.AllowFadeout, Info.Instrument.FadeOut);
    }

    /// <summary>Timeline jumped to <paramref name="position"/> inside this (synchronous, active) instrument: re-seek its voices.</summary>
    public void OnJump(double position)
    {
        double offset = Info.Instrument.StartOffset + (position - Info.RegionStart);
        for (Voice? v = Head; v is not null; v = v.NextInOwner)
        {
            if (v.Finished) continue;
            v.Position = v.Loop && v.LengthKnown && v.Length > 0 ? offset % v.Length : offset;
            if (v.IsReal && v.HasPlayed) _system.Commands.Add(new VoiceSeek(v.Id, v.Position));
        }
    }

    /// <summary>Stop from the owning instance: Immediate cuts; AllowFadeout fades over max(instrument fade, AHDSR releases).</summary>
    public void Stop(StopMode mode, float eventRelease)
    {
        _delayRemaining = -1;
        float fade = MathF.Max(Info.Instrument.FadeOut, MathF.Max(Info.MaxVolumeRelease, eventRelease));
        Cut(mode, fade);
    }

    public void KeyOff() => ModEval.Release(Info.Modulators, _mods);

    private void Cut(StopMode mode, float fade)
    {
        _scattering = false;
        if (mode == StopMode.Immediate)
        {
            Voice? prev = null;
            for (Voice? v = Head; v is not null;)
            {
                Voice? next = v.NextInOwner;
                StopVoice(prev, v);
                v = next;
            }
        }
        else
        {
            ModEval.Release(Info.Modulators, _mods);
            Voice? prev = null;
            for (Voice? v = Head; v is not null;)
            {
                Voice? next = v.NextInOwner;
                if (fade <= 0f) { StopVoice(prev, v); v = next; continue; }
                if (!v.FadingOut || v.FadeOutRemaining > fade)
                {
                    v.FadingOut = true;
                    v.FadeOutDuration = fade;
                    v.FadeOutRemaining = fade;
                }
                prev = v;
                v = next;
            }
        }
        for (int i = 0; i < Children.Count; i++) Children[i].Stop(mode);
        ReapChildren();
        if (_snapshot is not null)
        {
            _snapshot.Stop();
            _snapshot = null;
        }
    }

    // ---- firing --------------------------------------------------------------------------------------------------

    private void Fire(double offset)
    {
        ModEval.Init(Info.Modulators, _mods, _system.Random);
        switch (Info.Kind)
        {
            case InstrumentKind.Clip:
                StartVoice(Info.Clip!.Asset, offset, null);
                break;
            case InstrumentKind.Multi:
            {
                MultiInstrument multi = Info.Multi!;
                int pick = Pick(multi.Entries, multi.Mode, multi.AvoidRepeat);
                if (pick >= 0) StartVoice(multi.Entries[pick].Asset, offset, null);
                break;
            }
            case InstrumentKind.Scatterer:
                _scattering = true;
                _scatterTimer = 0;
                break;
            case InstrumentKind.Event:
                StartChild();
                break;
            case InstrumentKind.Programmer:
            {
                AssetRef asset = _instance.ResolveProgrammerAsset(Info.Programmer!.Key);
                if (!asset.IsEmpty) StartVoice(asset, offset, null);
                break;
            }
            case InstrumentKind.Command:
                _system.ExecuteCommand(Info.Command!, _instance);
                break;
            case InstrumentKind.Snapshot:
                _snapshot?.Stop();
                _snapshot = Info.SnapshotName is null ? null : _system.Mixer.StartSnapshot(Info.SnapshotName);
                break;
        }
    }

    private void StartChild()
    {
        EventDescription? nested = Info.NestedEvent;
        if (nested is null || _instance.Depth >= EventInstance.MaxDepth) return;
        int polyphony = Info.Instrument.Polyphony;
        while (polyphony > 0 && Children.Count >= polyphony)
        {
            EventInstance oldest = Children[0];
            oldest.Stop(StopMode.Immediate);
            Children.RemoveAt(0);
            oldest.Destroy();
        }
        EventInstance child = nested.CreateInstance(_instance);
        child.Set3DAttributes(_instance.Attributes);
        child.InheritedVolumeDb = _instance.EventVolumeDb + ChainVolumeDb;
        child.InheritedPitch = _instance.EventPitch * ChainPitch;
        child.Start();
        if (child.State == PlaybackState.Stopped) child.Destroy();
        else Children.Add(child);
    }

    private void StartVoice(AssetRef asset, double offset, Vector3? position)
    {
        if (asset.IsEmpty) return;
        Instrument inst = Info.Instrument;
        EventDescription desc = _instance.Description;
        EventMacros macros = desc.Event.Macros;

        double start = inst.StartOffset + offset;
        bool known = _system.Assets.TryGetLength(asset, out double length) && length > 0;
        if (known)
        {
            if (inst.Loop) start %= length;
            else if (start >= length) return;
        }

        int polyphony = inst.Polyphony;
        while (polyphony > 0 && VoiceCount >= polyphony) StopOldest();

        Voice v = _system.Voices.Acquire();
        v.Instance = _instance;
        v.Owner = this;
        v.Asset = asset;
        v.Bus = desc.BusName;
        v.BusIndex = desc.BusIndex;
        v.Loop = inst.Loop;
        v.Spatial = desc.Is3D;
        if (position.HasValue)
        {
            Attributes3D a = _instance.Attributes;
            v.Attributes = new Attributes3D(position.Value, a.Velocity, a.Forward, a.Up);
            v.OwnAttributes = true;
        }
        else
        {
            v.Attributes = _instance.Attributes;
        }
        v.MinDistance = macros.MinDistance;
        v.MaxDistance = macros.MaxDistance;
        v.Priority = macros.Priority;
        v.Position = start;
        v.Length = length;
        v.LengthKnown = known;
        v.RandomVolumeDb = inst.VolumeRandomDb != 0f ? _system.Random.NextSigned() * inst.VolumeRandomDb : 0f;
        v.RandomPitch = inst.PitchRandom != 0f ? MathF.Max(0.01f, 1f + _system.Random.NextSigned() * inst.PitchRandom) : 1f;
        v.FadeIn = inst.FadeIn;
        v.VolumeDb = _instance.EventVolumeDb + ChainVolumeDb + v.RandomVolumeDb + v.FadeDb();
        v.Pitch = _instance.EventPitch * ChainPitch * v.RandomPitch;
        v.Fresh = true;
        v.NextInOwner = Head;
        Head = v;
        VoiceCount++;
    }

    private void StopOldest()
    {
        Voice? prev = null;
        Voice? v = Head;
        if (v is null) return;
        while (v.NextInOwner is not null) { prev = v; v = v.NextInOwner; }
        StopVoice(prev, v);
    }

    /// <summary>Unlink and release; emits <see cref="VoiceStop"/> when the engine is currently playing it.</summary>
    private void StopVoice(Voice? prev, Voice v)
    {
        if (prev is null) Head = v.NextInOwner; else prev.NextInOwner = v.NextInOwner;
        VoiceCount--;
        if (v.IsReal && v.HasPlayed) _system.Commands.Add(new VoiceStop(v.Id));
        _instance.RaiseSoundStopped(v);
        _system.Voices.Release(v);
    }

    /// <summary>Engine reported the voice finished (or failed): drop it without emitting a stop.</summary>
    public void FinishVoice(Voice v)
    {
        Voice? prev = null;
        for (Voice? cur = Head; cur is not null; prev = cur, cur = cur.NextInOwner)
        {
            if (!ReferenceEquals(cur, v)) continue;
            if (prev is null) Head = v.NextInOwner; else prev.NextInOwner = v.NextInOwner;
            VoiceCount--;
            _instance.RaiseSoundStopped(v);
            _system.Voices.Release(v);
            return;
        }
    }

    // ---- playlist ------------------------------------------------------------------------------------------------

    private int Pick(List<MultiEntry> entries, PlaylistMode mode, int avoidRepeat)
    {
        int n = entries.Count;
        if (n == 0) return -1;
        if (_recent.Length < n) { Array.Resize(ref _recent, n); Array.Resize(ref _shuffle, n); _recentCount = 0; _recentHead = 0; _shuffleIndex = int.MaxValue; }
        int pick = -1;
        switch (mode)
        {
            case PlaylistMode.Sequential:
                for (int k = 0; k < n; k++)
                {
                    int i = (_sequential + k) % n;
                    if (entries[i].Weight <= 0f) continue;
                    pick = i;
                    _sequential = (i + 1) % n;
                    break;
                }
                break;
            case PlaylistMode.Shuffle:
                for (int attempts = 0; attempts < 2 && pick < 0; attempts++)
                {
                    if (_shuffleIndex >= n) Reshuffle(n);
                    while (_shuffleIndex < n)
                    {
                        int i = _shuffle[_shuffleIndex++];
                        if (entries[i].Weight > 0f) { pick = i; break; }
                    }
                }
                break;
            default:
                pick = PickRandom(entries, Math.Clamp(avoidRepeat, 0, n - 1));
                break;
        }
        if (pick >= 0) _lastPick = pick;
        return pick;
    }

    private void Reshuffle(int n)
    {
        for (int i = 0; i < n; i++) _shuffle[i] = i;
        for (int i = n - 1; i > 0; i--)
        {
            int j = _system.Random.NextInt(i + 1);
            (_shuffle[i], _shuffle[j]) = (_shuffle[j], _shuffle[i]);
        }
        if (n > 1 && _shuffle[0] == _lastPick)
        {
            int j = 1 + _system.Random.NextInt(n - 1);
            (_shuffle[0], _shuffle[j]) = (_shuffle[j], _shuffle[0]);
        }
        _shuffleIndex = 0;
    }

    private int PickRandom(List<MultiEntry> entries, int window)
    {
        int n = entries.Count;
        float total = 0f;
        for (int i = 0; i < n; i++)
            if (entries[i].Weight > 0f && !IsRecent(i, window)) total += entries[i].Weight;
        bool ignoreRecent = total <= 0f;
        if (ignoreRecent)
        {
            for (int i = 0; i < n; i++) if (entries[i].Weight > 0f) total += entries[i].Weight;
            if (total <= 0f) return -1;
        }
        float r = _system.Random.NextFloat() * total;
        int pick = -1;
        for (int i = 0; i < n; i++)
        {
            float w = entries[i].Weight;
            if (w <= 0f || (!ignoreRecent && IsRecent(i, window))) continue;
            pick = i;
            r -= w;
            if (r < 0f) break;
        }
        if (pick >= 0 && window > 0)
        {
            _recent[_recentHead] = pick;
            _recentHead = (_recentHead + 1) % window;
            if (_recentCount < window) _recentCount++;
        }
        return pick;
    }

    private bool IsRecent(int index, int window)
    {
        int count = Math.Min(_recentCount, window);
        for (int k = 0; k < count; k++) if (_recent[k] == index) return true;
        return false;
    }

    // ---- per-tick ------------------------------------------------------------------------------------------------

    public void Update(double dt, bool paused, float bpm)
    {
        Instrument inst = Info.Instrument;
        ModEval.Advance(Info.Modulators, _mods, paused ? 0 : dt, bpm, out float modVolume, out float modPitch);
        ChainVolumeDb = inst.VolumeDb + Info.Track.VolumeDb + ModEval.SumLanes(Info.VolumeLanes, _volumeLaneParams) + modVolume;
        ChainPitch = inst.Pitch * ModEval.ProductLanes(Info.PitchLanes, _pitchLaneParams) * modPitch;

        if (!paused)
        {
            if (_delayRemaining >= 0)
            {
                _delayRemaining -= dt;
                if (_delayRemaining <= 0)
                {
                    double late = -_delayRemaining;
                    _delayRemaining = -1;
                    if ((Active || inst.Async) && _instance.CanTrigger) Fire(_pendingOffset + late);
                }
            }
            if (_scattering)
            {
                ScattererInstrument scatterer = Info.Scatterer!;
                _scatterTimer -= dt;
                int guard = 0;
                while (_scatterTimer <= 0 && guard++ < 64)
                {
                    Spawn(scatterer);
                    float min = MathF.Max(0f, scatterer.MinInterval);
                    float max = MathF.Max(min, scatterer.MaxInterval);
                    _scatterTimer += min + _system.Random.NextFloat() * (max - min);
                    if (max <= 0f) { _scatterTimer = double.PositiveInfinity; break; }
                }
            }
        }

        float eventVolume = _instance.EventVolumeDb;
        float eventPitch = _instance.EventPitch;
        Voice? prev = null;
        for (Voice? v = Head; v is not null;)
        {
            Voice? next = v.NextInOwner;
            if (v.Finished)
            {
                if (prev is null) Head = next; else prev.NextInOwner = next;
                VoiceCount--;
                _instance.RaiseSoundStopped(v);
                _system.Voices.Release(v);
                v = next;
                continue;
            }
            if (v.Fresh)
            {
                // Triggered during this tick: its offset already matches the playhead; advancing again would double count.
                v.Fresh = false;
            }
            else if (!paused)
            {
                v.Age += dt;
                v.Position += dt * v.Pitch;
                if (v.LengthKnown)
                {
                    if (v.Loop) { if (v.Length > 0) v.Position %= v.Length; }
                    else if (v.Position >= v.Length) { StopVoice(prev, v); v = next; continue; }
                }
                if (v.FadingOut)
                {
                    v.FadeOutRemaining -= (float)dt;
                    if (v.FadeOutRemaining <= 0f) { StopVoice(prev, v); v = next; continue; }
                }
            }
            v.VolumeDb = eventVolume + ChainVolumeDb + v.RandomVolumeDb + v.FadeDb();
            v.Pitch = eventPitch * ChainPitch * v.RandomPitch;
            if (v.Spatial && !v.OwnAttributes) v.Attributes = _instance.Attributes;
            prev = v;
            v = next;
        }

        for (int i = 0; i < Children.Count;)
        {
            EventInstance child = Children[i];
            child.InheritedVolumeDb = eventVolume + ChainVolumeDb;
            child.InheritedPitch = eventPitch * ChainPitch;
            child.Set3DAttributes(_instance.Attributes);
            child.UpdateInternal(dt);
            if (child.State == PlaybackState.Stopped)
            {
                Children.RemoveAt(i);
                child.Destroy();
            }
            else
            {
                i++;
            }
        }
    }

    private void Spawn(ScattererInstrument scatterer)
    {
        if (scatterer.Entries.Count == 0) return;
        int pick = Pick(scatterer.Entries, scatterer.Mode, 1);
        if (pick < 0) return;
        float min = MathF.Max(0f, scatterer.MinScatterDistance);
        float max = MathF.Max(min, scatterer.MaxScatterDistance);
        float distance = min + _system.Random.NextFloat() * (max - min);
        float angle = _system.Random.NextFloat() * MathF.Tau;
        Vector3 center = _instance.Attributes.Position;
        var position = new Vector3(center.X + MathF.Cos(angle) * distance, center.Y, center.Z + MathF.Sin(angle) * distance);
        StartVoice(scatterer.Entries[pick].Asset, 0, position);
    }

    private void ReapChildren()
    {
        for (int i = 0; i < Children.Count;)
        {
            EventInstance child = Children[i];
            if (child.State == PlaybackState.Stopped)
            {
                Children.RemoveAt(i);
                child.Destroy();
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>Emit voice commands for this tick. Returns true when at least one owned voice is real.</summary>
    public bool Emit(CommandBuffer commands, bool paused)
    {
        bool anyReal = false;
        for (Voice? v = Head; v is not null; v = v.NextInOwner)
        {
            if (v.Finished || !v.IsReal) continue;
            anyReal = true;
            if (v.WantsPlay)
            {
                EventMacros macros = _instance.Description.Event.Macros;
                commands.Add(new VoicePlay(v.Id, v.Asset, v.Bus, v.Position, v.Loop, v.VolumeDb, v.Pitch, v.Spatial, v.Attributes, v.MinDistance, v.MaxDistance, Info.Instrument.SyncGroup, macros.Rolloff, macros.Doppler, macros.DopplerScale));
                v.WantsPlay = false;
                v.HasPlayed = true;
                v.LastVolumeDb = v.VolumeDb;
                v.LastPitch = v.Pitch;
                v.LastAttributes = v.Attributes;
                v.LastPaused = false;
                if (paused)
                {
                    commands.Add(new VoiceSetPaused(v.Id, true));
                    v.LastPaused = true;
                }
                _instance.RaiseSoundPlayed(v);
                continue;
            }
            if (v.VolumeDb != v.LastVolumeDb)
            {
                commands.Add(new VoiceSetVolume(v.Id, v.VolumeDb));
                v.LastVolumeDb = v.VolumeDb;
            }
            if (v.Pitch != v.LastPitch)
            {
                commands.Add(new VoiceSetPitch(v.Id, v.Pitch));
                v.LastPitch = v.Pitch;
            }
            if (v.Spatial && v.Attributes != v.LastAttributes)
            {
                commands.Add(new VoiceSet3D(v.Id, v.Attributes));
                v.LastAttributes = v.Attributes;
            }
            if (paused != v.LastPaused)
            {
                commands.Add(new VoiceSetPaused(v.Id, paused));
                v.LastPaused = paused;
            }
        }
        for (int i = 0; i < Children.Count; i++) anyReal |= Children[i].EmitInternal(commands);
        return anyReal;
    }
}
