# M1 contract — Dirge.Model runtime API

Shared types already exist and compile (do not change their shape without owner approval; add members only if required and note it in your report):
`addons/dirge/model/Project/*.cs` (AudioProject, MixerGraph, Bus, BusEffect, Vca, Snapshot, PropertyRef, ParameterDefinition, Curve, AutomationLane, Modulator*, AudioEvent, Track, Instrument*, LogicMarker*, TempoMarker), `Runtime/Commands.cs` (VoiceId, Attributes3D, Command records, Feedback records, CommandBuffer), `Runtime/Callbacks.cs`, `Runtime/ParameterBank.cs` (ParameterState, ParameterBank), `Evaluation/CurveMath.cs`, `Evaluation/Modulators.cs` (DirgeRandom, AhdsrState, LfoState).

Rules: `Dirge.Model` has **no Godot reference**, `TreatWarningsAsErrors`, net8.0 / C# 12, `ImplicitUsings` disabled. Zero allocations in steady-state `Update` (pool instances/voices, no LINQ in hot paths, no closures per tick). Deterministic for a seed: all randomness through the system's `DirgeRandom`.

## Public API (namespace `Dirge.Runtime`)

```csharp
public interface IAssetCatalog { bool TryGetLength(AssetRef asset, out double seconds); }  // default impl: AudioProject.AssetLengths

public enum StopMode { AllowFadeout, Immediate }
public enum PlaybackState { Starting, Playing, Sustaining, Stopping, Stopped }

public sealed class DirgeSystem
{
    public DirgeSystem(AudioProject project, int seed = 0, IAssetCatalog? assets = null);
    public AudioProject Project { get; }
    public MixerState Mixer { get; }
    public ParameterBank GlobalParameters { get; }
    public CommandBuffer Commands { get; }      // filled by Update; the DRIVER calls Commands.Clear() after applying
    public double Time { get; }                 // seconds, sum of dt
    public int InstanceCount { get; }
    public int RealVoiceCount { get; }
    public int MaxRealVoices { get; set; }      // default 64
    public void Feedback(Feedback feedback);    // VoiceFinished / VoiceFailed from the driver, applied at next Update
    public void Update(double dt);              // order: feedback → global params advance → instances (timeline, instruments, voices) → Mixer.Update(dt) → emit
    public bool TryGetEvent(string path, out EventDescription description);
    public EventDescription GetEvent(string path);      // throws KeyNotFoundException
    public EventDescription GetEvent(DirgeId id);
    public void SetGlobalParameter(string name, float value, bool immediate = false);
    public float GetGlobalParameter(string name);
    public void SetListener(int index, Attributes3D attributes, float weight = 1f);  // 0..7
    public void StopAll(StopMode mode);
}

public sealed class EventDescription
{
    public AudioEvent Event { get; }  public DirgeId Id { get; }  public string Path { get; }
    public bool Is3D { get; }  public bool IsOneshot { get; }   // oneshot = not Persistent and no loop region / looping instrument on timeline
    public double Length { get; }                                // timeline length: max(instrument end, marker end); 0 if parameter-sheet only
    public IReadOnlyList<ParameterDefinition> Parameters { get; }  // local
    public int InstanceCount { get; }
    public EventInstance CreateInstance();   // pooled; respects MaxInstances+Stealing at Start(), not here
}

public sealed class EventInstance
{
    public EventDescription Description { get; }
    public PlaybackState State { get; }
    public double TimelinePosition { get; set; }   // set = seek
    public bool IsVirtual { get; }
    public CallbackMask CallbackMask { get; set; }
    public event Action<EventInstance, CallbackEvent>? Callback;
    public void Start();                    // honours Cooldown, MaxInstances/Stealing, ScheduleLookahead=0 in M1 (immediate)
    public void Stop(StopMode mode);        // AllowFadeout = instrument FadeOut + AHDSR release; Immediate = VoiceStop now
    public void KeyOff();                   // releases SustainPoint hold + AHDSR modulators
    public void SetPaused(bool paused);
    public void SetParameter(string name, float value, bool immediate = false);  // local first, then global by name
    public float GetParameter(string name);
    public void SetParameterByLabel(string name, string label);
    public void Set3DAttributes(Attributes3D attributes);
    public void SetVolume(float db);  public void SetPitch(float ratio);
    public void SetProgrammerSound(AssetRef asset);   // used when a ProgrammerInstrument triggers and no callback handled it
    public void Release();                  // auto-destroy when Stopped
}

public sealed class MixerState
{
    // constructed by DirgeSystem; owns bus/vca/snapshot runtime state and emits Bus*/Effect* commands with change detection
    public string BusName(DirgeId bus);  public bool TryGetBus(string name, out DirgeId id);
    public float EffectiveBusVolumeDb(DirgeId bus);   // authored + snapshots + VCAs + automation + runtime override (not parent chain)
    public void SetBusVolume(string bus, float db);   // runtime override added on top (FMOD Bus::setVolume semantics)
    public void SetBusMute(string bus, bool mute);  public void SetBusSolo(string bus, bool solo);
    public void SetVcaVolume(string vca, float db);
    public SnapshotInstance StartSnapshot(string name);  // multiple instances allowed; intensity via AHDSR
    public void Update(double dt);                       // called by DirgeSystem; first call emits full state (force)
}

public sealed class SnapshotInstance
{
    public Snapshot Snapshot { get; }  public float Intensity { get; set; } // 0..1 scale multiplied with AHDSR
    public bool IsActive { get; }
    public void Stop();   // begins AHDSR release; removed when finished
}
```

## Semantics every slice must agree on

- **Voice ids**: `DirgeSystem` allocates `VoiceId` from a free list; driver maps id→player. A `VoicePlay` for an id already playing is a restart.
- **Gain chain** (dB summed): instrument + track + event macro + instance SetVolume + event automation/modulators + instrument automation/modulators + fades → `VoiceSetVolume`. Bus volume is **not** baked into voices; it's emitted via `BusSetVolume`. Parent chain is the engine's job.
- **Bus volume** (dB): authored + Σ VCAs + Σ snapshot contributions + automation (global params) + runtime override. Snapshot blending: sort active by Priority desc; for each targeted property, `Blending` snapshots lerp authored→value by intensity in priority order; `Overriding` with intensity>0 replaces lower-priority results and continues lerping by intensity.
- **Mute/solo**: authored ∨ runtime override; `BusSetMute`/`BusSetSolo` emitted with change detection. Solo propagation is the engine's.
- **Effects**: `EffectSetParameter(bus, index, name, value)` only when value changed (float equality). Driver instantiates the effect chain at load from `MixerGraph`; the model never emits "create effect".
- **Pitch**: ratio (1 = unchanged). Pitch chain multiplied → `VoiceSetPitch`.
- **Stealing**: at `Start`, if `MaxInstances` reached: Oldest stops the oldest instance (Immediate + Stolen callback), Quietest = lowest effective instance gain, Furthest = greatest distance to listener 0, Virtualize = new instance becomes virtual (no voices, timeline still runs), None = Start is a no-op. Global `MaxRealVoices`: lowest Priority number (most important) keeps voices; others virtualize.
- **Timeline**: `dt` advances all non-paused instances; `LoopRegion` wraps; `TransitionMarker/Region` jump per `Quantization` using the event's `Tempo` list; `SustainPoint` holds until `KeyOff`; `DestinationMarker` → `MarkerCallback`; `BeatCallback` each beat if masked. Timeline instruments trigger when position enters `[Start, Start+Length)`; parameter-sheet instruments trigger when the parameter's **effective Value** enters its range; `Async` instruments keep playing on exit, otherwise `CutMode` applies. Seek quantization of the engine is the driver's problem; the model emits exact seconds.
- **Spatial**: `Is3D` events emit `VoicePlay(Spatial: true, Attributes)` and `VoiceSet3D` when attributes change. Built-in parameters are computed per instance from listener 0 (weighted listeners: nearest for M1) and written into the local bank as `Set(value, immediate: true)` **before** instruments evaluate.
- **One-shot end**: when `IAssetCatalog` knows the length, the model schedules voice end itself and ignores a later `VoiceFinished`; otherwise it waits for `VoiceFinished`. An instance with no voices, timeline past `Length`, not Persistent, no pending triggers → `Stopped` + `StoppedCallback`.

## File ownership

| Slice | Files |
|---|---|
| Instances | `model/Runtime/DirgeSystem.cs`, `EventDescription.cs`, `EventInstance.cs`, `Timeline.cs`, `InstrumentPlayback.cs`, `VoicePool.cs`, `Spatial.cs` |
| Mixer | `model/Runtime/MixerState.cs`, `SnapshotInstance.cs`, `Evaluation/PropertyBlend.cs` |
| Serialization+Validation | `model/Serialization/*.cs`, `model/Validation/*.cs`, `tests/Dirge.Model.Tests/**` |
| Godot runtime | `addons/dirge/runtime/*.cs`, `demo/**`, `scripts/*.sh`, `README.md` |

Tests for Instances/Mixer live in `tests/Dirge.Model.Tests/Runtime/` and `tests/Dirge.Model.Tests/Mixer/` respectively (those two slices own their own test subfolders; the Serialization slice owns the rest of `tests/`).
