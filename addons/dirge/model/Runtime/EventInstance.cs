using System;
using System.Collections.Generic;
using Dirge.Project;

namespace Dirge.Runtime;

public enum StopMode { AllowFadeout, Immediate }

public enum PlaybackState { Starting, Playing, Sustaining, Stopping, Stopped }

/// <summary>
/// One playing event. Pooled by its <see cref="EventDescription"/>; owns a local <see cref="ParameterBank"/>, the
/// <see cref="Timeline"/>, per-instrument runtime and the event half of the gain/pitch chain. Nested instances
/// (event instruments) are updated by their parent's instrument, not by the system.
/// </summary>
public sealed class EventInstance
{
    /// <summary>Maximum nesting depth of event instruments (root = 0).</summary>
    internal const int MaxDepth = 8;

    private static readonly StartedCallback StartedEvent = new();
    private static readonly StoppedCallback StoppedEvent = new();
    private static readonly VirtualizedCallback VirtualizedEvent = new();
    private static readonly UnvirtualizedCallback UnvirtualizedEvent = new();
    private static readonly StolenCallback StolenEvent = new();

    private readonly DirgeSystem _system;
    private readonly EventDescription _description;
    private readonly ParameterBank _locals;
    private readonly ParameterState[] _builtIns;
    private readonly BuiltInParameter[] _builtInKinds;
    private readonly InstrumentState[] _instruments;
    private readonly ModRuntime[] _eventMods;
    private readonly ParameterState?[] _eventVolumeLaneParams;
    private readonly ParameterState?[] _eventPitchLaneParams;
    private readonly Timeline _timeline;

    private PlaybackState _state = PlaybackState.Stopped;
    private bool _paused;
    private bool _released;
    private bool _destroyed;
    private bool _isVirtual;
    private bool _seekPending;
    private double _seekTo;
    private float _volumeDb;
    private float _pitch = 1f;
    private AssetRef _programmerAsset;
    private Attributes3D _attributes = Attributes3D.Origin;
    private double _startTime;
    /// <summary>Seconds left before a quantized start begins playing; ≤ 0 = start at the next update.</summary>
    private double _startDelay;

    /// <summary>Virtualized by <see cref="StealingMode.Virtualize"/>: voices never get players while set.</summary>
    internal bool StealVirtual;
    internal EventInstance? Parent;
    internal int Depth;
    /// <summary>Gain/pitch inherited from the parent's event instrument chain (nested instances).</summary>
    internal float InheritedVolumeDb;
    internal float InheritedPitch = 1f;
    /// <summary>Macros + SetVolume + event automation + event modulators + inherited, this tick.</summary>
    internal float EventVolumeDb;
    internal float EventPitch = 1f;

    public EventDescription Description => _description;
    public PlaybackState State => _state;
    public bool IsVirtual => _isVirtual;
    public CallbackMask CallbackMask { get; set; } = CallbackMask.All;
    public event Action<EventInstance, CallbackEvent>? Callback;

    /// <summary>Seconds. Setting seeks at the next update; instruments under the new position re-trigger or re-seek. Magnet regions snap the target.</summary>
    public double TimelinePosition
    {
        get => _seekPending ? _seekTo : _timeline.Position;
        set
        {
            _system.Recorder?.Call(this, "Seek", null, null, value, false, 0);
            _seekTo = Math.Max(0, value);
            if (_state == PlaybackState.Stopped || _state == PlaybackState.Starting) _timeline.Reset(_seekTo);
            else _seekPending = true;
        }
    }

    /// <summary>True while a transition timeline (<see cref="TransitionMarker.TransitionLength"/>) is running between source and destination.</summary>
    public bool IsInTransition => _timeline.Transition is not null;
    /// <summary>Seconds elapsed on the running transition timeline; 0 when none runs.</summary>
    public double TransitionPosition => _timeline.Transition is null ? 0 : _timeline.TransitionElapsed;

    internal Attributes3D Attributes => _attributes;
    internal bool Paused => _paused;
    internal bool IsDestroyed => _destroyed;
    internal double StartTime => _startTime;
    internal bool CanTrigger => _state == PlaybackState.Playing || _state == PlaybackState.Sustaining;
    internal ParameterBank LocalParameters => _locals;

    internal EventInstance(DirgeSystem system, EventDescription description)
    {
        _system = system;
        _description = description;
        _locals = new ParameterBank(description.Event.LocalParameters);
        _builtIns = new ParameterState[description.BuiltInParameters.Length];
        _builtInKinds = new BuiltInParameter[description.BuiltInParameters.Length];
        for (int i = 0; i < _builtIns.Length; i++)
        {
            ParameterDefinition d = description.Event.LocalParameters[description.BuiltInParameters[i]];
            _locals.TryGet(d.Id, out _builtIns[i]);
            _builtInKinds[i] = d.BuiltIn;
        }
        _eventMods = new ModRuntime[description.EventModulators.Length];
        ModEval.BindParameters(description.EventModulators, _eventMods, this);
        _eventVolumeLaneParams = new ParameterState?[description.EventVolumeLanes.Length];
        _eventPitchLaneParams = new ParameterState?[description.EventPitchLanes.Length];
        for (int i = 0; i < _eventVolumeLaneParams.Length; i++) _eventVolumeLaneParams[i] = ResolveParameter(description.EventVolumeLanes[i].Parameter);
        for (int i = 0; i < _eventPitchLaneParams.Length; i++) _eventPitchLaneParams[i] = ResolveParameter(description.EventPitchLanes[i].Parameter);
        _instruments = new InstrumentState[description.Instruments.Length];
        for (int i = 0; i < _instruments.Length; i++) _instruments[i] = new InstrumentState(description.Instruments[i], this, system);
        _timeline = new Timeline(this, description);
    }

    /// <summary>Fresh state for a (re)used pooled instance.</summary>
    internal void Reinitialize()
    {
        IReadOnlyList<ParameterState> all = _locals.All;
        for (int i = 0; i < all.Count; i++)
        {
            all[i].Set(all[i].Definition.Default, true);
            all[i].Rebase();
        }
        for (int i = 0; i < _instruments.Length; i++) _instruments[i].Reset();
        _timeline.Reset(0);
        _state = PlaybackState.Stopped;
        _paused = false;
        _released = false;
        _destroyed = false;
        _isVirtual = false;
        _seekPending = false;
        _seekTo = 0;
        _volumeDb = 0f;
        _pitch = 1f;
        _programmerAsset = default;
        _attributes = Attributes3D.Origin;
        _startTime = 0;
        _startDelay = 0;
        StealVirtual = false;
        Parent = null;
        Depth = 0;
        InheritedVolumeDb = 0f;
        InheritedPitch = 1f;
        EventVolumeDb = _description.Event.Macros.VolumeDb;
        EventPitch = _description.Event.Macros.Pitch;
        CallbackMask = CallbackMask.All;
        Callback = null;
    }

    internal void AttachToParent(EventInstance parent)
    {
        Parent = parent;
        Depth = parent.Depth + 1;
    }

    // ---- public API ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Honours Cooldown and MaxInstances/Stealing. Playback begins at the next <see cref="DirgeSystem.Update"/>, or — with
    /// <see cref="EventMacros.StartQuantization"/> and a playing <see cref="DirgeSystem.Conductor"/> that has a tempo map — at
    /// the conductor's next grid point (the first one at least <see cref="EventMacros.ScheduleLookahead"/> seconds away).
    /// </summary>
    public void Start()
    {
        _system.Recorder?.Call(this, "Start", null, null, 0, false, 0);
        if (_destroyed || _state != PlaybackState.Stopped) return;
        EventMacros macros = _description.Event.Macros;
        if (macros.Cooldown > 0f && _system.Time - _description.LastStartTime < macros.Cooldown) return;
        if (macros.MaxInstances > 0 && _description.Started.Count >= macros.MaxInstances && !Steal(macros.Stealing)) return;
        _description.LastStartTime = _system.Time;
        _startTime = _system.Time;
        _startDelay = QuantizedStartDelay(macros);
        _state = PlaybackState.Starting;
        ModEval.Init(_description.EventModulators, _eventMods, _system.Random);
        _description.OnStarted(this);
    }

    private double QuantizedStartDelay(EventMacros macros)
    {
        if (macros.StartQuantization == Quantization.None) return 0;
        EventInstance? conductor = _system.Conductor;
        if (conductor is null || ReferenceEquals(conductor, this) || !conductor.CanTrigger) return 0;
        return conductor.DelayToNextGrid(macros.StartQuantization, macros.ScheduleLookahead);
    }

    /// <summary>Seconds from this instance's playhead to its next grid point at least <paramref name="lookahead"/> seconds away; 0 when it has no grid.</summary>
    internal double DelayToNextGrid(Quantization quantization, float lookahead)
    {
        double position = _timeline.Position;
        if (!_description.NextGrid(position, quantization, false, out double at)) return 0;
        double delay = Math.Max(0, at - position);
        int guard = 0;
        while (delay < lookahead && guard++ < 64 && _description.NextGrid(at, quantization, false, out double after) && after > at)
        {
            at = after;
            delay = at - position;
        }
        return delay;
    }

    /// <returns>true when the new instance may start.</returns>
    private bool Steal(StealingMode mode)
    {
        IReadOnlyList<EventInstance> started = _description.Started;
        switch (mode)
        {
            case StealingMode.None:
                return false;
            case StealingMode.Virtualize:
                StealVirtual = true;
                return true;
        }
        EventInstance? victim = null;
        double best = 0;
        for (int i = 0; i < started.Count; i++)
        {
            EventInstance candidate = started[i];
            if (candidate._state == PlaybackState.Stopping) { victim = candidate; break; }
            double score = mode switch
            {
                StealingMode.Quietest => -candidate.EventVolumeDb,
                StealingMode.Furthest => candidate.DistanceToNearestListener(),
                _ => _system.Time - candidate._startTime,
            };
            if (victim is null || score > best) { victim = candidate; best = score; }
        }
        if (victim is null) return false;
        victim.Raise(CallbackMask.Stolen, StolenEvent);
        IApiSink? recorder = _system.Recorder;
        recorder?.EnterInternal();
        victim.Stop(StopMode.Immediate);
        recorder?.ExitInternal();
        return true;
    }

    /// <summary>AllowFadeout: instrument fade-outs and AHDSR releases run first. Immediate: every voice stops now.</summary>
    public void Stop(StopMode mode)
    {
        _system.Recorder?.Call(this, "Stop", null, null, 0, false, (int)mode);
        if (_destroyed || _state == PlaybackState.Stopped) return;
        if (_state == PlaybackState.Starting)
        {
            Finish();
            return;
        }
        if (mode == StopMode.Immediate)
        {
            for (int i = 0; i < _instruments.Length; i++) _instruments[i].Stop(StopMode.Immediate, 0f);
            Finish();
            return;
        }
        if (_state == PlaybackState.Stopping) return;
        _state = PlaybackState.Stopping;
        _timeline.KeyOff();
        ModEval.Release(_description.EventModulators, _eventMods);
        for (int i = 0; i < _instruments.Length; i++) _instruments[i].Stop(StopMode.AllowFadeout, _description.EventMaxRelease);
        if (!AnyInstrumentBusy()) Finish();
    }

    /// <summary>Release a <see cref="SustainPoint"/> hold and begin AHDSR releases.</summary>
    public void KeyOff()
    {
        _system.Recorder?.Call(this, "KeyOff", null, null, 0, false, 0);
        if (_destroyed) return;
        bool wasHolding = _timeline.Holding;
        _timeline.KeyOff();
        if (wasHolding && _state == PlaybackState.Sustaining) _state = PlaybackState.Playing;
        ModEval.Release(_description.EventModulators, _eventMods);
        for (int i = 0; i < _instruments.Length; i++) _instruments[i].KeyOff();
    }

    public void SetPaused(bool paused)
    {
        _system.Recorder?.Call(this, "SetPaused", null, null, 0, paused, 0);
        if (_paused == paused) return;
        _paused = paused;
        for (int i = 0; i < _instruments.Length; i++)
        {
            List<EventInstance> children = _instruments[i].Children;
            for (int c = 0; c < children.Count; c++) children[c].SetPaused(paused);
        }
    }

    /// <summary>Local parameter first, then global by name.</summary>
    public void SetParameter(string name, float value, bool immediate = false)
    {
        _system.Recorder?.Call(this, "SetParameter", name, null, value, immediate, 0);
        if (_locals.TryGet(name, out ParameterState state) || _system.GlobalParameters.TryGet(name, out state)) state.Set(value, immediate);
    }

    public float GetParameter(string name)
    {
        if (_locals.TryGet(name, out ParameterState state) || _system.GlobalParameters.TryGet(name, out state)) return state.Value;
        return 0f;
    }

    /// <summary>Rate of change of the parameter's effective value in units per second over the last update (<see cref="ParameterState.Velocity"/>); 0 when unknown.</summary>
    public float GetParameterVelocity(string name)
    {
        if (_locals.TryGet(name, out ParameterState state) || _system.GlobalParameters.TryGet(name, out state)) return state.Velocity;
        return 0f;
    }

    public void SetParameterByLabel(string name, string label)
    {
        _system.Recorder?.Call(this, "SetParameterByLabel", name, label, 0, false, 0);
        if (!_locals.TryGet(name, out ParameterState state) && !_system.GlobalParameters.TryGet(name, out state)) return;
        List<string> labels = state.Definition.Labels;
        for (int i = 0; i < labels.Count; i++)
        {
            if (!string.Equals(labels[i], label, StringComparison.Ordinal)) continue;
            state.Set(state.Definition.Minimum + i);
            return;
        }
    }

    public void Set3DAttributes(Attributes3D attributes)
    {
        _system.Recorder?.Call3D(this, "Set3DAttributes", 0, attributes, 0f);
        _attributes = attributes;
    }

    /// <summary>dB added to every voice of this instance.</summary>
    public void SetVolume(float db)
    {
        _system.Recorder?.Call(this, "SetVolume", null, null, db, false, 0);
        _volumeDb = db;
    }

    /// <summary>Ratio multiplied into every voice of this instance.</summary>
    public void SetPitch(float ratio)
    {
        _system.Recorder?.Call(this, "SetPitch", null, null, ratio, false, 0);
        _pitch = MathF.Max(0.01f, ratio);
    }

    /// <summary>Asset used when a <see cref="ProgrammerInstrument"/> triggers and neither an audio table nor a callback supplied one.</summary>
    public void SetProgrammerSound(AssetRef asset)
    {
        _system.Recorder?.Call(this, "SetProgrammerSound", asset.Path, null, 0, false, 0);
        _programmerAsset = asset;
    }

    /// <summary>Destroy when Stopped (now if already stopped).</summary>
    public void Release()
    {
        _system.Recorder?.Call(this, "Release", null, null, 0, false, 0);
        if (_destroyed) return;
        _released = true;
        if (_state == PlaybackState.Stopped && Parent is null) _system.RequestDestroy(this);
    }

    // ---- lifecycle -----------------------------------------------------------------------------------------------

    private void Finish()
    {
        _state = PlaybackState.Stopped;
        _timeline.KeyOff();
        _timeline.Reset(_timeline.Position);
        _seekPending = false;
        _description.OnStopped(this);
        if (_description.Event.IsConductor) _system.OnConductorStopped(this);
        if (_isVirtual || StealVirtual)
        {
            _isVirtual = false;
            StealVirtual = false;
        }
        Raise(CallbackMask.Stopped, StoppedEvent);
        if (_released && Parent is null) _system.RequestDestroy(this);
    }

    /// <summary>Return to the description pool. Only valid when Stopped.</summary>
    internal void Destroy()
    {
        if (_destroyed) return;
        if (_state != PlaybackState.Stopped) Stop(StopMode.Immediate);
        _destroyed = true;
        Callback = null;
        _description.Recycle(this);
    }

    private bool AnyInstrumentBusy()
    {
        for (int i = 0; i < _instruments.Length; i++) if (_instruments[i].IsBusy) return true;
        return false;
    }

    private bool AnyActiveSnapshot()
    {
        for (int i = 0; i < _instruments.Length; i++) if (_instruments[i].HasActiveSnapshot) return true;
        return false;
    }

    // ---- per-tick ------------------------------------------------------------------------------------------------

    internal void UpdateInternal(double dt)
    {
        if (_destroyed || _state == PlaybackState.Stopped) return;
        bool justStarted = false;
        double advance = dt;
        if (_state == PlaybackState.Starting)
        {
            if (_startDelay > 0)
            {
                _startDelay -= dt;
                if (_startDelay > 0) return;
                // The grid point fell inside this tick: play only the part after it.
                advance = Math.Min(dt, -_startDelay);
                _startDelay = 0;
            }
            _state = PlaybackState.Playing;
            justStarted = true;
            if (_description.Event.IsConductor && Parent is null) _system.OnConductorPlaying(this);
            Raise(CallbackMask.Started, StartedEvent);
        }
        if (_seekPending)
        {
            _seekPending = false;
            if (_state == PlaybackState.Sustaining) _state = PlaybackState.Playing;
            _timeline.Reset(_seekTo);
            justStarted = true;
        }

        if (_description.Is3D) WriteBuiltIns();
        _locals.Advance(dt);

        float bpm = _description.BpmAt(_timeline.Position);
        ModEval.Advance(_description.EventModulators, _eventMods, _paused ? 0 : dt, bpm, out float modVolume, out float modPitch);
        EventMacros macros = _description.Event.Macros;
        EventVolumeDb = macros.VolumeDb + _volumeDb + ModEval.SumLanes(_description.EventVolumeLanes, _eventVolumeLaneParams) + modVolume + InheritedVolumeDb;
        EventPitch = macros.Pitch * _pitch * ModEval.ProductLanes(_description.EventPitchLanes, _eventPitchLaneParams) * modPitch * InheritedPitch;

        if (justStarted && CanTrigger && !_timeline.Begin()) OnTimelineJump(_timeline.Position);
        if (!_paused && CanTrigger) _timeline.Advance(advance);
        if (CanTrigger) EvaluateParameterInstruments();

        for (int i = 0; i < _instruments.Length; i++) _instruments[i].Update(dt, _paused, bpm);

        if (_state == PlaybackState.Stopping)
        {
            if (!AnyInstrumentBusy()) Finish();
        }
        else if (_state == PlaybackState.Playing && !macros.Persistent && !_timeline.Holding && _timeline.Transition is null
                 && _timeline.Position >= _description.Length && !AnyInstrumentBusy() && !AnyActiveSnapshot())
        {
            Finish();
        }
    }

    /// <summary>Emit this tick's voice commands; raises Virtualized/Unvirtualized on change. Returns true when any voice is real.</summary>
    internal bool EmitInternal(CommandBuffer commands)
    {
        if (_destroyed) return false;
        bool anyReal = false;
        int voices = 0;
        for (int i = 0; i < _instruments.Length; i++)
        {
            anyReal |= _instruments[i].Emit(commands, _paused);
            voices += _instruments[i].VoiceCount + _instruments[i].Children.Count;
        }
        bool isVirtual = _state != PlaybackState.Stopped && (StealVirtual || (voices > 0 && !anyReal));
        if (isVirtual != _isVirtual)
        {
            _isVirtual = isVirtual;
            Raise(isVirtual ? CallbackMask.Virtualized : CallbackMask.Unvirtualized, isVirtual ? VirtualizedEvent : UnvirtualizedEvent);
        }
        return anyReal;
    }

    private void WriteBuiltIns()
    {
        if (_builtIns.Length == 0) return;
        Attributes3D listener = _system.NearestListener(_attributes.Position);
        EventMacros macros = _description.Event.Macros;
        for (int i = 0; i < _builtIns.Length; i++)
            _builtIns[i].Set(Spatial.Compute(_builtInKinds[i], _attributes, listener, macros.MinDistance, macros.MaxDistance), true);
    }

    private void EvaluateParameterInstruments()
    {
        for (int i = 0; i < _instruments.Length; i++)
        {
            InstrumentState s = _instruments[i];
            if (s.Info.OnTimeline || s.Info.Transition is not null) continue;
            ParameterState? p = s.SheetParameter;
            if (p is null) continue;
            float v = p.Value;
            double start = s.Info.RegionStart;
            double end = s.Info.RegionEnd;
            // Half-open [start, end), except that a range reaching the parameter's maximum includes it.
            bool inRegion = end <= start
                ? v == (float)start
                : v >= start && (v < end || (v == (float)end && end >= p.Definition.Maximum));
            if (inRegion && !s.Active) s.OnEnter(0);
            else if (!inRegion && s.Active) s.OnExit();
        }
    }

    // ---- timeline hooks ------------------------------------------------------------------------------------------

    /// <summary>
    /// Playhead moved from <paramref name="from"/> to <paramref name="to"/>. <paramref name="loopingHere"/>: a loop end at
    /// <paramref name="to"/> is about to jump back, so regions are treated as (start, end] — instruments ending exactly at the
    /// loop end stay active (the jump re-seeks them) and instruments starting there are not flashed on.
    /// </summary>
    internal void OnTimelineSegment(double from, double to, bool inclusiveStart, bool loopingHere = false)
    {
        for (int i = 0; i < _instruments.Length; i++)
        {
            InstrumentState s = _instruments[i];
            if (!s.Info.OnTimeline || s.Info.Transition is not null) continue;
            if (s.Info.IsPoint)
            {
                double p = s.Info.RegionStart;
                bool crossed = (inclusiveStart ? p >= from : p > from) && p <= to;
                if (crossed) s.OnPoint();
                continue;
            }
            bool inRegion = loopingHere
                ? to > s.Info.RegionStart && to <= s.Info.RegionEnd
                : to >= s.Info.RegionStart && to < s.Info.RegionEnd;
            if (inRegion && !s.Active) s.OnEnter(to - s.Info.RegionStart);
            else if (!inRegion && s.Active) s.OnExit();
            else if (!inRegion && !s.Active && !loopingHere
                     && (inclusiveStart ? s.Info.RegionStart >= from : s.Info.RegionStart > from)
                     && s.Info.RegionEnd <= to)
            {
                // Entered and left within one tick (region shorter than dt): fire once like a point so short
                // one-shots are never swallowed by a long frame; the voice plays its natural length.
                s.OnPoint();
            }
        }
    }

    internal void OnTimelineJump(double position)
    {
        for (int i = 0; i < _instruments.Length; i++)
        {
            InstrumentState s = _instruments[i];
            if (!s.Info.OnTimeline || s.Info.Transition is not null) continue;
            if (s.Info.IsPoint)
            {
                if (position == s.Info.RegionStart) s.OnPoint();
                continue;
            }
            bool inRegion = position >= s.Info.RegionStart && position < s.Info.RegionEnd;
            if (inRegion && !s.Active) s.OnEnter(position - s.Info.RegionStart);
            else if (!inRegion && s.Active) s.OnExit();
            else if (inRegion && !s.Info.Instrument.Async) s.OnJump(position);
        }
    }

    /// <summary>Transition timeline started: cut the source timeline's instruments (async ones keep playing) and enter transition instruments at 0.</summary>
    internal void OnTransitionBegin(LogicMarker marker)
    {
        for (int i = 0; i < _instruments.Length; i++)
        {
            InstrumentState s = _instruments[i];
            if (!s.Info.OnTimeline) continue;
            if (s.Info.Transition is null)
            {
                if (s.Active) s.OnExit();
                continue;
            }
            if (!ReferenceEquals(s.Info.Transition, marker)) continue;
            if (s.Info.IsPoint)
            {
                if (s.Info.RegionStart == 0) s.OnPoint();
                continue;
            }
            if (s.Info.RegionStart <= 0 && 0 < s.Info.RegionEnd && !s.Active) s.OnEnter(-s.Info.RegionStart);
        }
    }

    /// <summary>Transition clock moved from <paramref name="from"/> (exclusive) to <paramref name="to"/>.</summary>
    internal void OnTransitionSegment(LogicMarker marker, double from, double to)
    {
        for (int i = 0; i < _instruments.Length; i++)
        {
            InstrumentState s = _instruments[i];
            if (!ReferenceEquals(s.Info.Transition, marker)) continue;
            if (s.Info.IsPoint)
            {
                double p = s.Info.RegionStart;
                if (p > from && p <= to) s.OnPoint();
                continue;
            }
            bool inRegion = to >= s.Info.RegionStart && to < s.Info.RegionEnd;
            if (inRegion && !s.Active) s.OnEnter(to - s.Info.RegionStart);
            else if (!inRegion && s.Active) s.OnExit();
        }
    }

    /// <summary>Transition clock ended (or was cancelled by a seek): the transition instruments leave their regions.</summary>
    internal void OnTransitionEnd(LogicMarker marker)
    {
        for (int i = 0; i < _instruments.Length; i++)
        {
            InstrumentState s = _instruments[i];
            if (ReferenceEquals(s.Info.Transition, marker) && s.Active) s.OnExit();
        }
    }

    internal void OnSustain()
    {
        if (_state == PlaybackState.Playing) _state = PlaybackState.Sustaining;
    }

    internal void OnMarker(DestinationMarker marker)
    {
        if (WantsCallback(CallbackMask.TimelineMarker)) Callback!(this, new MarkerCallback(marker.Name, marker.Position));
    }

    internal void OnBeat(int bar, int beat, float bpm, double position)
    {
        if (WantsCallback(CallbackMask.TimelineBeat)) Callback!(this, new BeatCallback(bar, beat, bpm, position));
    }

    internal bool MarkerPasses(LogicMarker marker) => ConditionsPass(marker.Conditions) && _system.Random.Chance(marker.Probability);

    internal bool ConditionsPass(List<TriggerCondition> conditions)
    {
        for (int i = 0; i < conditions.Count; i++)
        {
            TriggerCondition c = conditions[i];
            ParameterState? p = ResolveParameter(c.Parameter);
            if (p is null) return false;
            float v = p.Value;
            if (v < c.Minimum || v > c.Maximum) return false;
        }
        return true;
    }

    internal ParameterState? ResolveParameter(DirgeId id)
    {
        if (id.IsNone) return null;
        if (_locals.TryGet(id, out ParameterState state)) return state;
        return _system.GlobalParameters.TryGet(id, out state) ? state : null;
    }

    /// <summary>
    /// Programmer asset resolution order: <see cref="AudioProject.AudioTables"/> for the system <see cref="DirgeSystem.Locale"/>
    /// (falling back to the key's first locale) → <see cref="ProgrammerSoundCallback"/> → <see cref="SetProgrammerSound"/>.
    /// </summary>
    internal AssetRef ResolveProgrammerAsset(string key)
    {
        AssetRef asset = _system.ResolveTableAsset(key);
        if (!asset.IsEmpty) return asset;
        if (WantsCallback(CallbackMask.ProgrammerSound))
        {
            var request = new ProgrammerSoundCallback(key);
            Callback!(this, request);
            asset = request.Asset;
        }
        if (asset.IsEmpty) asset = _programmerAsset;
        // Non-table results go to the recorder so a replay (same tables, no game callbacks) can answer identically.
        _system.Recorder?.ProgrammerSound(this, key, asset);
        return asset;
    }

    internal double DistanceToNearestListener()
    {
        if (!_description.Is3D) return 0;
        Attributes3D listener = _system.NearestListener(_attributes.Position);
        return (listener.Position - _attributes.Position).Length();
    }

    internal void RaiseSoundPlayed(Voice v)
    {
        if (WantsCallback(CallbackMask.SoundPlayed)) Callback!(this, new SoundPlayedCallback(v.Id, v.Asset));
    }

    internal void RaiseSoundStopped(Voice v)
    {
        if (WantsCallback(CallbackMask.SoundStopped)) Callback!(this, new SoundStoppedCallback(v.Id));
    }

    private bool WantsCallback(CallbackMask kind) => Callback is not null && (CallbackMask & kind) != 0;

    private void Raise(CallbackMask kind, CallbackEvent ev)
    {
        if (WantsCallback(kind)) Callback!(this, ev);
    }
}
