using System;
using System.Collections.Generic;
using Dirge.Evaluation;
using Dirge.Project;

namespace Dirge.Runtime;

/// <summary>
/// Runtime state of the mixer graph: bus/VCA runtime overrides, snapshot instances and global-parameter automation on mixer
/// properties. <see cref="Update"/> evaluates every bus volume/mute/solo/bypass, bus pitch, VCA and effect parameter and emits
/// <c>Bus*</c>/<c>Effect*</c> commands with change detection (the first call emits the full state).
/// <para>
/// Property chain (dB summed, ratios/effect parameters in their own units):
/// authored → snapshots (<see cref="PropertyBlend"/>, ascending priority) → automation (curve value added) → modulators (LFO/Random) → runtime override;
/// bus volume additionally sums the final value of every VCA listed on the bus. The parent bus chain is the engine's job.
/// </para>
/// <para>
/// Steady-state <see cref="Update"/> allocates only the command records it emits: all per-tick state lives in arrays indexed by
/// precomputed slots; dictionaries are touched only by the by-name/by-id API.
/// </para>
/// </summary>
public sealed class MixerState
{
    private readonly struct EffectSlot
    {
        public readonly int Bus;
        public readonly int Effect;
        public readonly string Parameter;
        /// <summary>When ≥ 0 this is a <c>pitch_scale</c> on the bus's pitch-shift effect; the bus pitch property is multiplied in.</summary>
        public readonly int PitchBus;
        public EffectSlot(int bus, int effect, string parameter, int pitchBus) { Bus = bus; Effect = effect; Parameter = parameter; PitchBus = pitchBus; }
    }

    private readonly struct EffectRef
    {
        public readonly int Bus;
        public readonly int Index;
        public readonly BusEffect Effect;
        public EffectRef(int bus, int index, BusEffect effect) { Bus = bus; Index = index; Effect = effect; }
    }

    private readonly struct AutomationSlot
    {
        public readonly DirgeId Lane;
        public readonly int Property;
        public readonly ParameterState Parameter;
        public readonly Curve Curve;
        public AutomationSlot(DirgeId lane, int property, ParameterState parameter, Curve curve) { Lane = lane; Property = property; Parameter = parameter; Curve = curve; }
    }

    /// <summary>One <see cref="AudioProject.MixerModulators"/> entry resolved to its property slot. A class so the LFO phase mutates in place.</summary>
    private sealed class ModulatorSlot
    {
        public readonly Modulator Modulator;
        public readonly int Property;
        /// <summary>Bus pitch is a ratio: the contribution multiplies as (1 + x) instead of adding.</summary>
        public readonly bool Multiply;
        public readonly LfoModulator? Lfo;
        public readonly float Random;
        public LfoState LfoState;

        public ModulatorSlot(Modulator modulator, int property, bool multiply, DirgeRandom random)
        {
            Modulator = modulator;
            Property = property;
            Multiply = multiply;
            Lfo = modulator as LfoModulator;
            if (Lfo is not null) LfoState = new LfoState(Lfo, random);
            else Random = random.NextSigned();
        }
    }

    private const string PitchShiftEffectType = "AudioEffectPitchShift";
    private const string PitchScaleParameter = "pitch_scale";
    private const float MinPitch = 0.01f;

    private readonly Bus[] _buses;
    private readonly string[] _busNames;
    private readonly Vca[] _vcas;
    private readonly EffectRef[] _effects;
    private readonly EffectSlot[] _effectSlots;
    private readonly int[][] _busVcaProperties;
    /// <summary>Index of the bus's pitch-shift effect, or -1.</summary>
    private readonly int[] _busPitchEffect;
    /// <summary>True when the bus pitch is merged into an explicit <c>pitch_scale</c> effect slot instead of emitted standalone.</summary>
    private readonly bool[] _busPitchMerged;

    private readonly int _busCount;
    private readonly int _vcaCount;
    private readonly int _effectSlotBase;

    // Property table: [0,B) bus volume dB, [B,2B) bus pitch ratio, [2B,2B+V) VCA dB, [2B+V, …) effect parameters.
    private readonly float[] _authored;
    private readonly float[] _value;

    private readonly float[] _busVolumeOverride;
    private readonly bool[] _busMuteOverride;
    private readonly bool[] _busSoloOverride;
    private readonly float[] _vcaOverride;

    private readonly float[] _lastBusVolume;
    private readonly bool[] _lastBusMute;
    private readonly bool[] _lastBusSolo;
    private readonly bool[] _lastBusBypass;
    private readonly float[] _lastBusPitch;
    private readonly bool[] _lastEffectEnabled;
    private readonly float[] _lastEffectValue;
    private bool _forceEmit = true;

    private readonly Dictionary<DirgeId, int> _busById = new();
    private readonly Dictionary<string, int> _busByName = new(StringComparer.Ordinal);
    private readonly Dictionary<DirgeId, int> _vcaById = new();
    private readonly Dictionary<string, int> _vcaByName = new(StringComparer.Ordinal);
    private readonly Dictionary<PropertyRef, int> _propertyByRef = new();
    private readonly Dictionary<string, SnapshotInstance.Definition> _snapshotsByName = new(StringComparer.Ordinal);

    /// <summary>Active snapshot instances kept sorted by priority ascending (ties: start order).</summary>
    private readonly List<SnapshotInstance> _active = new();
    private readonly Stack<SnapshotInstance> _pool = new();
    private readonly List<AutomationSlot> _automation = new();
    private readonly List<ModulatorSlot> _modulators = new();

    private readonly ParameterBank _globals;
    private readonly CommandBuffer _commands;

    /// <summary>System random; consumed at construction by Random mixer modulators and Noise LFOs so the stream is seed-deterministic.</summary>
    internal DirgeRandom Random { get; }
    /// <summary>Owning system (set by <see cref="DirgeSystem"/>); reached for the API recorder.</summary>
    internal DirgeSystem? System { get; set; }
    private IApiSink? Recorder => System?.Recorder;

    /// <summary>
    /// <paramref name="platform"/> selects the <see cref="AudioProject.PlatformOverrides"/> entry (case-insensitive) whose values
    /// replace the authored baseline before snapshots and automation; null or no match keeps the authored values.
    /// <see cref="AudioProject.MixerAutomation"/> lanes are registered at construction (lanes with an unknown target or a
    /// non-global parameter are skipped; the validator reports them).
    /// </summary>
    public MixerState(AudioProject project, ParameterBank globals, CommandBuffer commands, DirgeRandom random, string? platform = null)
    {
        _globals = globals;
        _commands = commands;
        Random = random;

        MixerGraph graph = project.Mixer;
        _busCount = graph.Buses.Count;
        _vcaCount = graph.Vcas.Count;
        _buses = graph.Buses.ToArray();
        _vcas = graph.Vcas.ToArray();
        _busNames = new string[_busCount];
        _busPitchEffect = new int[_busCount];
        _busPitchMerged = new bool[_busCount];

        int effectCount = 0;
        int slotCount = 0;
        for (int i = 0; i < _busCount; i++)
        {
            Bus bus = _buses[i];
            _busNames[i] = bus.Name;
            _busById[bus.Id] = i;
            _busByName[bus.Name] = i;
            _busPitchEffect[i] = -1;
            for (int e = 0; e < bus.Effects.Count; e++)
            {
                effectCount++;
                slotCount += bus.Effects[e].Parameters.Count;
                if (_busPitchEffect[i] < 0 && bus.Effects[e].TypeName == PitchShiftEffectType) _busPitchEffect[i] = e;
            }
        }
        for (int v = 0; v < _vcaCount; v++)
        {
            _vcaById[_vcas[v].Id] = v;
            _vcaByName[_vcas[v].Name] = v;
        }

        _effectSlotBase = 2 * _busCount + _vcaCount;
        int propertyCount = _effectSlotBase + slotCount;
        _authored = new float[propertyCount];
        _value = new float[propertyCount];
        _effects = new EffectRef[effectCount];
        _effectSlots = new EffectSlot[slotCount];
        _lastEffectEnabled = new bool[effectCount];
        _lastEffectValue = new float[slotCount];

        int effectIndex = 0;
        int slot = 0;
        for (int i = 0; i < _busCount; i++)
        {
            Bus bus = _buses[i];
            _authored[BusVolumeProperty(i)] = bus.VolumeDb;
            _authored[BusPitchProperty(i)] = 1f;
            _propertyByRef[PropertyRef.BusVolume(bus.Id)] = BusVolumeProperty(i);
            _propertyByRef[PropertyRef.BusPitch(bus.Id)] = BusPitchProperty(i);
            for (int e = 0; e < bus.Effects.Count; e++)
            {
                BusEffect effect = bus.Effects[e];
                _effects[effectIndex++] = new EffectRef(i, e, effect);
                foreach (KeyValuePair<string, float> parameter in effect.Parameters)
                {
                    bool isPitch = e == _busPitchEffect[i] && parameter.Key == PitchScaleParameter;
                    if (isPitch) _busPitchMerged[i] = true;
                    _effectSlots[slot] = new EffectSlot(i, e, parameter.Key, isPitch ? i : -1);
                    _authored[_effectSlotBase + slot] = parameter.Value;
                    _propertyByRef[PropertyRef.Effect(bus.Id, e, parameter.Key)] = _effectSlotBase + slot;
                    slot++;
                }
            }
        }
        for (int v = 0; v < _vcaCount; v++)
        {
            _authored[VcaProperty(v)] = _vcas[v].VolumeDb;
            _propertyByRef[PropertyRef.VcaVolume(_vcas[v].Id)] = VcaProperty(v);
        }

        _busVcaProperties = new int[_busCount][];
        var scratch = new List<int>();
        for (int i = 0; i < _busCount; i++)
        {
            scratch.Clear();
            List<DirgeId> vcas = _buses[i].Vcas;
            for (int k = 0; k < vcas.Count; k++)
                if (_vcaById.TryGetValue(vcas[k], out int v)) scratch.Add(VcaProperty(v));
            _busVcaProperties[i] = scratch.ToArray();
        }

        ApplyPlatformOverrides(project, platform);
        Array.Copy(_authored, _value, propertyCount);
        _busVolumeOverride = new float[_busCount];
        _busMuteOverride = new bool[_busCount];
        _busSoloOverride = new bool[_busCount];
        _vcaOverride = new float[_vcaCount];
        _lastBusVolume = new float[_busCount];
        _lastBusMute = new bool[_busCount];
        _lastBusSolo = new bool[_busCount];
        _lastBusBypass = new bool[_busCount];
        _lastBusPitch = new float[_busCount];

        foreach (Snapshot snapshot in project.Snapshots)
        {
            var values = new SnapshotInstance.ResolvedValue[snapshot.Values.Count];
            for (int k = 0; k < values.Length; k++)
            {
                SnapshotValue sv = snapshot.Values[k];
                values[k] = new SnapshotInstance.ResolvedValue(_propertyByRef.TryGetValue(sv.Target, out int p) ? p : -1, sv.Value);
            }
            _snapshotsByName[snapshot.Name] = new SnapshotInstance.Definition(snapshot, values);
        }

        for (int i = 0; i < project.MixerAutomation.Count; i++)
        {
            AutomationLane lane = project.MixerAutomation[i];
            if (_propertyByRef.TryGetValue(lane.Target, out int property) && _globals.TryGet(lane.Parameter, out ParameterState parameter))
                _automation.Add(new AutomationSlot(lane.Id, property, parameter, lane.Curve));
        }
        for (int i = 0; i < project.MixerModulators.Count; i++)
        {
            Modulator m = project.MixerModulators[i];
            if (m is (LfoModulator or RandomModulator) && _propertyByRef.TryGetValue(m.Target, out int property))
                _modulators.Add(new ModulatorSlot(m, property, m.Target.Kind == PropertyKind.BusPitch, random));
        }
    }

    private void ApplyPlatformOverrides(AudioProject project, string? platform)
    {
        if (string.IsNullOrEmpty(platform)) return;
        for (int i = 0; i < project.PlatformOverrides.Count; i++)
        {
            PlatformOverride po = project.PlatformOverrides[i];
            if (!string.Equals(po.Platform, platform, StringComparison.OrdinalIgnoreCase)) continue;
            for (int k = 0; k < po.Values.Count; k++)
                if (_propertyByRef.TryGetValue(po.Values[k].Target, out int p)) _authored[p] = po.Values[k].Value;
        }
    }

    private int BusVolumeProperty(int bus) => bus;
    private int BusPitchProperty(int bus) => _busCount + bus;
    private int VcaProperty(int vca) => 2 * _busCount + vca;

    public int BusCount => _busCount;
    public int ActiveSnapshotCount => _active.Count;

    public string BusName(DirgeId bus) => _busNames[BusIndex(bus)];
    public bool TryGetBus(string name, out DirgeId id)
    {
        if (_busByName.TryGetValue(name, out int i)) { id = _buses[i].Id; return true; }
        id = DirgeId.None;
        return false;
    }
    public bool TryGetVca(string name, out DirgeId id)
    {
        if (_vcaByName.TryGetValue(name, out int v)) { id = _vcas[v].Id; return true; }
        id = DirgeId.None;
        return false;
    }

    /// <summary>Bus volume as of the last <see cref="Update"/>: authored + snapshots + automation + VCAs + runtime override (not the parent chain).</summary>
    public float EffectiveBusVolumeDb(DirgeId bus) => _value[BusVolumeProperty(BusIndex(bus))];
    /// <summary>Bus pitch ratio as of the last <see cref="Update"/> (1 = unchanged).</summary>
    public float EffectiveBusPitch(DirgeId bus) => _value[BusPitchProperty(BusIndex(bus))];
    /// <summary>VCA volume as of the last <see cref="Update"/>: authored + snapshots + automation + runtime override.</summary>
    public float EffectiveVcaVolumeDb(DirgeId vca) => _value[VcaProperty(VcaIndex(vca))];

    /// <summary>Runtime offset added on top of the evaluated bus volume (FMOD <c>Bus::setVolume</c> semantics).</summary>
    public void SetBusVolume(string bus, float db)
    {
        Recorder?.Call(this, "SetBusVolume", bus, null, db, false, 0);
        _busVolumeOverride[BusIndex(bus)] = db;
    }

    public void SetBusMute(string bus, bool mute)
    {
        Recorder?.Call(this, "SetBusMute", bus, null, 0, mute, 0);
        _busMuteOverride[BusIndex(bus)] = mute;
    }

    public void SetBusSolo(string bus, bool solo)
    {
        Recorder?.Call(this, "SetBusSolo", bus, null, 0, solo, 0);
        _busSoloOverride[BusIndex(bus)] = solo;
    }

    /// <summary>Runtime offset added on top of the evaluated VCA volume.</summary>
    public void SetVcaVolume(string vca, float db)
    {
        Recorder?.Call(this, "SetVcaVolume", vca, null, db, false, 0);
        _vcaOverride[VcaIndex(vca)] = db;
    }

    public bool HasSnapshot(string name) => _snapshotsByName.ContainsKey(name);

    /// <summary>Start a snapshot by name. Multiple instances of the same snapshot may run at once.</summary>
    public SnapshotInstance StartSnapshot(string name)
    {
        if (!_snapshotsByName.TryGetValue(name, out SnapshotInstance.Definition? def))
            throw new KeyNotFoundException($"Snapshot '{name}' not found.");
        SnapshotInstance instance = _pool.Count > 0 ? _pool.Pop() : new SnapshotInstance();
        instance.Activate(def, this);
        int priority = def.Snapshot.Priority;
        int insertAt = _active.Count;
        while (insertAt > 0 && _active[insertAt - 1].Def.Snapshot.Priority > priority) insertAt--;
        _active.Insert(insertAt, instance);
        Recorder?.Created(instance, "StartSnapshot", name);
        return instance;
    }

    /// <summary>
    /// Drive a mixer property (bus volume/pitch, VCA volume, effect parameter) from a global parameter. The curve value is
    /// added to the property in its own units after snapshots and before runtime overrides. <see cref="AudioProject.MixerAutomation"/>
    /// lanes are registered at construction; this adds a lane at runtime (tool live-edit) and throws on invalid targets.
    /// </summary>
    public void AddAutomation(AutomationLane lane)
    {
        if (!_propertyByRef.TryGetValue(lane.Target, out int property))
            throw new ArgumentException($"Automation target {lane.Target} is not a mixer property of this project.", nameof(lane));
        if (!_globals.TryGet(lane.Parameter, out ParameterState parameter))
            throw new ArgumentException($"Automation parameter {lane.Parameter} is not a global parameter.", nameof(lane));
        _automation.Add(new AutomationSlot(lane.Id, property, parameter, lane.Curve));
    }

    public bool RemoveAutomation(DirgeId laneId)
    {
        for (int i = 0; i < _automation.Count; i++)
        {
            if (_automation[i].Lane == laneId) { _automation.RemoveAt(i); return true; }
        }
        return false;
    }

    /// <summary>
    /// Add an LFO or Random modulator on a mixer property at runtime (tool live-edit); <see cref="AudioProject.MixerModulators"/>
    /// entries are registered at construction. The Random offset / Noise seed is drawn from the system generator now.
    /// </summary>
    public void AddModulator(Modulator modulator)
    {
        if (modulator is not (LfoModulator or RandomModulator))
            throw new ArgumentException("Mixer modulators must be LFO or Random.", nameof(modulator));
        if (!_propertyByRef.TryGetValue(modulator.Target, out int property))
            throw new ArgumentException($"Modulator target {modulator.Target} is not a mixer property of this project.", nameof(modulator));
        _modulators.Add(new ModulatorSlot(modulator, property, modulator.Target.Kind == PropertyKind.BusPitch, Random));
    }

    public bool RemoveModulator(DirgeId modulatorId)
    {
        for (int i = 0; i < _modulators.Count; i++)
        {
            if (_modulators[i].Modulator.Id == modulatorId) { _modulators.RemoveAt(i); return true; }
        }
        return false;
    }

    /// <summary>Evaluate the whole mixer and emit changed values. The first call emits everything.</summary>
    public void Update(double dt)
    {
        // 1. Snapshot envelopes; drop finished instances (list stays priority-sorted).
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            SnapshotInstance instance = _active[i];
            instance.Envelope.Advance(dt);
            if (instance.Envelope.IsFinished)
            {
                _active.RemoveAt(i);
                instance.Deactivate();
                _pool.Push(instance);
            }
        }

        // 2. Authored baseline.
        Array.Copy(_authored, _value, _value.Length);

        // 3. Snapshots, lowest priority first so the highest priority is applied last.
        for (int i = 0; i < _active.Count; i++)
        {
            SnapshotInstance instance = _active[i];
            float intensity = instance.EffectiveIntensity;
            if (intensity <= 0f) continue;
            SnapshotMode mode = instance.Def.Snapshot.Mode;
            SnapshotInstance.ResolvedValue[] values = instance.Def.Values;
            for (int k = 0; k < values.Length; k++)
            {
                int p = values[k].Property;
                if (p < 0) continue;
                _value[p] = PropertyBlend.Apply(_value[p], _authored[p], values[k].Value, intensity, mode);
            }
        }

        // 4. Automation from global parameters.
        for (int i = 0; i < _automation.Count; i++)
        {
            AutomationSlot a = _automation[i];
            _value[a.Property] += CurveMath.Evaluate(a.Curve, a.Parameter.Value);
        }

        // 4b. Modulators: LFO phase advances with dt (TempoSync has no tempo map here and reads RateHz as Hz); Random is a
        // fixed offset drawn at construction. dB / effect units add; bus pitch multiplies.
        for (int i = 0; i < _modulators.Count; i++)
        {
            ModulatorSlot m = _modulators[i];
            float wave = m.Lfo is null ? m.Random : dt > 0 ? m.LfoState.Advance(dt, m.Lfo.RateHz) : m.LfoState.Current;
            float contribution = m.Modulator.Amount * wave;
            if (m.Multiply) _value[m.Property] *= MathF.Max(MinPitch, 1f + contribution);
            else _value[m.Property] += contribution;
        }

        // 5. Runtime overrides and VCA sums.
        for (int v = 0; v < _vcaCount; v++) _value[VcaProperty(v)] += _vcaOverride[v];
        for (int i = 0; i < _busCount; i++)
        {
            float db = _value[i] + _busVolumeOverride[i];
            int[] vcas = _busVcaProperties[i];
            for (int k = 0; k < vcas.Length; k++) db += _value[vcas[k]];
            _value[i] = db;
        }

        // 6. Emit with change detection.
        bool force = _forceEmit;
        for (int i = 0; i < _busCount; i++)
        {
            Bus bus = _buses[i];
            string name = _busNames[i];
            float volume = _value[i];
            if (force || volume != _lastBusVolume[i]) { _lastBusVolume[i] = volume; _commands.Add(new BusSetVolume(name, volume)); }
            bool mute = bus.Mute | _busMuteOverride[i];
            if (force || mute != _lastBusMute[i]) { _lastBusMute[i] = mute; _commands.Add(new BusSetMute(name, mute)); }
            bool solo = bus.Solo | _busSoloOverride[i];
            if (force || solo != _lastBusSolo[i]) { _lastBusSolo[i] = solo; _commands.Add(new BusSetSolo(name, solo)); }
            bool bypass = bus.BypassEffects;
            if (force || bypass != _lastBusBypass[i]) { _lastBusBypass[i] = bypass; _commands.Add(new BusSetBypass(name, bypass)); }
            int pitchEffect = _busPitchEffect[i];
            if (pitchEffect >= 0 && !_busPitchMerged[i])
            {
                float pitch = _value[BusPitchProperty(i)];
                if (force || pitch != _lastBusPitch[i]) { _lastBusPitch[i] = pitch; _commands.Add(new EffectSetParameter(name, pitchEffect, PitchScaleParameter, pitch)); }
            }
        }
        for (int e = 0; e < _effects.Length; e++)
        {
            EffectRef effect = _effects[e];
            bool enabled = effect.Effect.Enabled;
            if (force || enabled != _lastEffectEnabled[e]) { _lastEffectEnabled[e] = enabled; _commands.Add(new EffectSetEnabled(_busNames[effect.Bus], effect.Index, enabled)); }
        }
        for (int s = 0; s < _effectSlots.Length; s++)
        {
            EffectSlot slot = _effectSlots[s];
            float value = _value[_effectSlotBase + s];
            if (slot.PitchBus >= 0) value *= _value[BusPitchProperty(slot.PitchBus)];
            if (force || value != _lastEffectValue[s]) { _lastEffectValue[s] = value; _commands.Add(new EffectSetParameter(_busNames[slot.Bus], slot.Effect, slot.Parameter, value)); }
        }
        _forceEmit = false;
    }

    private int BusIndex(DirgeId bus) => _busById.TryGetValue(bus, out int i) ? i : throw new KeyNotFoundException($"Bus {bus} not found.");
    private int BusIndex(string bus) => _busByName.TryGetValue(bus, out int i) ? i : throw new KeyNotFoundException($"Bus '{bus}' not found.");
    private int VcaIndex(DirgeId vca) => _vcaById.TryGetValue(vca, out int i) ? i : throw new KeyNotFoundException($"VCA {vca} not found.");
    private int VcaIndex(string vca) => _vcaByName.TryGetValue(vca, out int i) ? i : throw new KeyNotFoundException($"VCA '{vca}' not found.");
}
