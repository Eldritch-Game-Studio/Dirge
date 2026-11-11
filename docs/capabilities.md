# Dirge capabilities and runtime API

Feature matrix from [`SPEC.md`](SPEC.md) §3 with the implementation status of each row as of 1.0.0, followed by a reference of every public member of the runtime types game code talks to. Statuses: **Done** — implemented and covered by a model test, the headless smoke or a tool proof; **Partial** — implemented with a documented gap; **Planned** — not implemented.

Priority tiers: P0 ships with 1.0, P1 in 1.x, P2 after; `—` is a deliberate non-goal.

## Feature matrix

### Events

| FMOD | Dirge | P | Status | Note |
|---|---|---|---|---|
| Event (timeline + parameter sheets) | `AudioEvent`: `Tracks`, `Instrument.Placement` on the timeline or a parameter sheet, `Macros` | P0 | Done | |
| Instance lifecycle (create / start / stop immediate·fade-out / release) | `EventDescription.CreateInstance`, `EventInstance.Start/Stop(StopMode)/Release`, `PlaybackState` | P0 | Done | |
| Nested / referenced events | `EventInstrument` → another `AudioEvent` | P0 | Done | depth limit 8; cycles rejected by `ProjectValidator` |
| Macros: priority, max instances, stealing (oldest / quietest / furthest / virtualize / none), cooldown, persistent, min/max distance, rolloff, doppler, lookahead | `EventMacros`; `VoicePlay.Rolloff/Doppler/DopplerScale` | P0 | Done | `AudioDriver.ApplySpatialMacros` sets `attenuation_model`, `unit_size`, `max_distance`, `doppler_tracking` on each `AudioStreamPlayer3D`; `DopplerScale` only switches doppler on (> 0) or off — Godot has no scale |
| Voice stealing at global polyphony cap | `DirgeSystem.MaxRealVoices` + `Bus.MaxVoices` | P0 | Done | over-cap instances virtualize by priority |
| Timeline markers: loop region, transition marker, transition region, sustain point, destination marker, tempo marker, magnet region | `LoopRegion`, `TransitionMarker`, `TransitionRegion`, `SustainPoint`, `DestinationMarker`, `TempoMarker`, `MagnetRegion` | P0 (magnet P1) | Done | seeks are block-quantized by Godot (~10 ms at default latency) |
| Trigger conditions: parameter range, probability | `TriggerCondition`, `Instrument.Probability`, `LogicMarker.Probability` | P0 | Done | labeled parameters are matched by their numeric value |
| Async instruments, cut-off on condition exit | `Instrument.Async`, `CutMode` (Cut / FadeOut) | P0 | Done | |
| Callbacks: started / stopped / marker / beat / sound played / sound stopped / virtualized / unvirtualized / stolen / programmer sound | `EventInstance.Callback` + `CallbackMask`, `CallbackEvent` records; GDScript: `DirgeServer` signals | P0 | Done | handle-based instances emit `EventStarted`, `EventStopped`, `TimelineMarker`, `TimelineBeat`, `SoundPlayed`, `EventVirtualized`, `EventUnvirtualized`, `EventStolen`, `ProgrammerSound`; `SoundStopped` is C#-only |
| Event preview / audition with parameter scrubbing | Transport pane | P0 | Done | |
| Per-event 3D attributes | `EventInstance.Set3DAttributes(Attributes3D)` → `AudioStreamPlayer3D` | P0 | Done | |
| Instance user data / property overrides (`setProperty`) | — | P1 | Planned | |

### Instruments

| FMOD | Dirge | P | Status | Note |
|---|---|---|---|---|
| Single | `ClipInstrument` | P0 | Done | any `AudioStream` resource |
| Multi (random / sequential / shuffle, avoid-repeat, weights) | `MultiInstrument`, `PlaylistMode`, `MultiEntry` | P0 | Done | model-side pick, deterministic under `Seed` |
| Scatterer | `ScattererInstrument` | P0 | Done | |
| Programmer | `ProgrammerInstrument`; `AudioTable` lookup, then `ProgrammerSoundCallback`, then `EventInstance.SetProgrammerSound` | P0 | Done | |
| Event / nested | `EventInstrument` | P0 | Done | |
| Command (start / stop event, set parameter) | `CommandInstrument`, `CommandAction` | P0 | Done | |
| Snapshot | `SnapshotInstrument` | P0 | Done | |
| Silence | `SilenceInstrument` | P0 | Done | |
| Plugin instrument (synth) | — | P2 | Planned | |
| Properties: volume, pitch, random volume/pitch, start offset, loop, fade in/out, cut mode, polyphony, trigger delay, probability, sync group | `Instrument` | P0 | Done | |
| Per-asset import hints (streaming, compression) | — | P1 | Planned | use Godot's import dock |

### Music / adaptive

| FMOD | Dirge | P | Status | Note |
|---|---|---|---|---|
| Beat-synced transitions | tempo map + model clock | P0 | Partial | `AudioStreamInteractive` is deliberately not used; transitions seek on the model clock (≤ 1 mix block), not sample-accurately |
| Vertical layering with per-layer automation | `Instrument.SyncGroup` → one `AudioStreamSynchronized` per group | P0 | Done | layers in a group start sample-locked |
| Stingers quantized to music | `EventMacros.StartQuantization`, `ScheduleLookahead`, `AudioEvent.IsConductor`, `DirgeSystem.Conductor` | P0 | Done | |
| Tempo map (tempos, time signatures) | `TempoMarker` (`Bpm`, `BeatsPerBar`) | P0 | Done | |
| Beat / bar callbacks | `BeatCallback` | P0 | Done | |
| Playlist | `MultiInstrument` with `PlaylistMode.Sequential`/`Shuffle` | P0 | Partial | no `AudioStreamPlaylist`; gapless only inside a sync group |

### Parameters

| FMOD | Dirge | P | Status | Note |
|---|---|---|---|---|
| Continuous, discrete, labeled | `ParameterKind` | P0 | Done | `EventInstance.SetParameterByLabel` |
| Local and global | `AudioEvent.LocalParameters`, `AudioProject.GlobalParameters` | P0 | Done | |
| Seek speed (asymmetric), hold | `SeekSpeedUp`, `SeekSpeedDown`, `Hold` | P0 | Done | |
| Velocity | `EventInstance.GetParameterVelocity` | P1 | Done | |
| Built-ins: distance, distance normalized, direction, elevation, cone angle, orientation, speed, speed absolute | `BuiltInParameter` | P0 | Done | from the nearest enabled listener in the emitter's spatial region; positive weights enable, not weighted mixing |
| Automation of any property (linear / smooth / stepped curves) | `AutomationLane`, `Curve`, `CurveShape`, `PropertyRef` | P0 | Done | mixer, event and instrument properties |
| Modulators: random, AHDSR, LFO (sine / square / triangle / saw / noise, tempo sync), sidechain, autopitch | `RandomModulator`, `AhdsrModulator`, `LfoModulator`, `AutopitchModulator` (`AutopitchMath`); `AudioProject.MixerModulators` (LFO/Random on bus volume/pitch, VCA, effect parameters, evaluated by `MixerState`) | P0 (sidechain P2) | Done | sidechain modulator Planned (P2); mixer LFOs read `RateHz` as Hz (no tempo map) |
| Conditions on instruments, transitions, logic markers | `TriggerCondition` | P0 | Done | |

### Mixer

| FMOD | Dirge | P | Status | Note |
|---|---|---|---|---|
| Bus hierarchy, master | `MixerGraph.Buses`, `Bus.Parent` → `AudioServer` | P0 | Done | Master stays at index 0 |
| Group bus volume, pitch, mute, solo, bypass | `Bus`; pitch through an `AudioEffectPitchShift` in the chain | P0 | Done | |
| Return buses + sends (pre / post fader) | `DirgeAudioEffectSend` / `DirgeAudioEffectReturn` (native) | P2 | Done | requires the GDExtension; no degraded fallback without it |
| VCAs | `Vca`, `Bus.Vcas` | P0 | Done | dB summation onto bus volume |
| Effects | every instantiable `AudioEffect` by `TypeName`; `BusEffect.Parameters`/`StringParameters` | P0 | Done | tremolo = `LfoModulator` in `AudioProject.MixerModulators` on a bus volume (mixer strip `LFO` button) or any effect parameter; transceiver = send/return; convolution and LUFS below |
| Pre / post fader effect position | `Bus.FaderIndex` (default `int.MaxValue` = after every effect); `AudioDriver.FaderOf`, `EngineEffectIndex`; mixer pane `FADER` row (drag to move) | P0 | Done | fader inside the chain = `AudioEffectAmplify` at that engine slot, bus volume held at 0 dB, `BusSetVolume` drives the amplify; effects after the fader shift one engine slot (model indices unchanged); bypass mirrors the fader volume onto the bus; validator: `bus-fader-index-negative` |
| Snapshots: intensity blending, priority, overriding vs blending, scoped properties, AHDSR intensity | `Snapshot`, `SnapshotMode`, `SnapshotValue`, `Ahdsr`; `SnapshotInstance` | P0 | Done | |
| Sidechain compressor | `AudioEffectCompressor` with its `sidechain` string parameter | P0 | Done | |
| Ducking presets | "Ducking preset…" in the mixer add-effect popup → source-bus picker → `AudioEffectCompressor` with `sidechain` = source (−24 dB, 8:1, 20 ms, 250 ms, gain 0), named "Ducking (from <bus>)"; `EffectCatalog.DuckingPreset`; sandbox also ships a `Dialogue Ducking` snapshot | P0 | Done | |
| Spatializer | `AudioStreamPlayer3D` + `AudioListener3D` | P0 (HRTF —) | Done | HRTF is a non-goal |
| Rolloff | `EventMacros.Rolloff` → `attenuation_model` (`Inverse`, `InverseSquare`, `Logarithmic`, `Disabled`); custom via `Distance` built-in → volume automation | P0 | Done | FMOD linear / linear-squared / inverse-tapered shapes have no engine model: use `Disabled` plus automation |
| Doppler | `EventMacros.Doppler`, `DopplerScale` → `doppler_tracking` (`PhysicsStep` when on and scale > 0) | P0 | Done | scale magnitude is authored for parity only |
| Occlusion / obstruction | game-supplied parameter + low-pass automation | P1 | Partial | no template; author it as any other parameter |
| Per-platform mixer overrides | `PlatformOverride`, `DirgeServer.Platform` | P1 | Done | windows, macos, linux, android, ios, web |
| LUFS M/S/I + true peak | `LoudnessMeter` (BS.1770-4), `LoudnessCapture` on Master, `DirgeStats` | P1 | Done | |
| Peak meters on every bus | Mixer/Profiler panes via `AudioServer.GetBusPeakVolumeLeftDb` | P0 | Done | |

### Tooling

Selection updates reuse browser rows/tag chips, the event toolbar and empty automation controls, unchanged transport parameter lists, and structurally unchanged mixer effect rows. Pickers resolve the current event when opened; unrelated project changes do not refresh the mixer. Parameter-definition changes and structural effect-chain changes still rebuild their affected controls.

The responsiveness measurements in the Unreleased changelog were collected with the companion NecronomiDOM style-reuse changes in the maintained Eldritch editor, not the stock-Godot smoke environment. To reproduce the combined improvements from source, use NecronomiDOM's matching source changes rather than the 1.1.5 release fetched by `scripts/fetch-deps.sh`; Dirge's stock Godot 4.7.2 .NET build target is unchanged.

| FMOD Studio | Dirge | P | Status | Note |
|---|---|---|---|---|
| Event editor: timeline, tracks, instruments, parameter sheets, markers, automation | `EventEditorPane` | P0 | Done | |
| Mixer view | `MixerPane` | P0 | Done | |
| Browser with search and tags | `BrowserPane`; `AudioEvent.Tags`, `EventTree.Build(tag:)`/`AllTags` | P0 | Done | tag chips under the search box filter the events tree; add/remove tags on the selected event in the Events footer |
| Transport with 3D preview panner | `TransportPane`, `PreviewPanner` | P0 | Done | |
| Profiler: live view, recording, replay | `ProfilerPane`; `Recorder`, `RecordingJson`, `Replayer.Replay/Compare` | P0 / P1 | Done | recordings are JSON test fixtures |
| Live update | `LiveUpdateHost` (game), `LiveUpdateBridge`/`LiveUpdateClient` (editor); `--dirge-live[=port]` | P1 | Done | localhost TCP, protocol 1, port 7447 |
| Sandbox | `demo/sandbox/sandbox.tscn`; `DirgeOverlay` (F9) | P1 | Done | |
| Validation panel + CLI | `ValidationPanel`, `ProjectValidator`, `tools/dirge-validate` | P0 | Done | |
| Scripting | C# API + GDScript through `DirgeServer` | P0 | Done | |
| Undo/redo, multi-select, copy/paste across events | `ProjectDocument`, `ToolCommand` | P0 | Done | clipboard is pane-local and survives event switches |
| Localization audio tables | `AudioTable`, `DirgeSystem.Locale` | P1 | Done | |
| Banks | `AudioBank`; `DirgeServer.LoadBank/UnloadBank/IsBankLoaded`, `BankLoader` | — | Done | preload groups over `ResourceLoader`; no bank files |
| Version control | text `*.dirge.json` | — | Done | |
| Hi-DPI / editor scale | `ToolScale` (`Core`); `dirge/ui_scale` editor setting | — | Done | one scale for every document host and canvas: editor display scale × `main_font_size` / 14, or `dirge/ui_scale` verbatim when > 0 (0 = auto); re-applied live on settings/DPI changes. Hosts mount through `ToolDocuments.Slot` — never as direct children of a Godot container, which resets their scale |
| Layout preferences | `ToolSettings` (`Core`): `dirge/header_sections`, `dirge/split_browser`, `dirge/split_bottom` | — | Done | event header sections start collapsed and persist; browser/center and top/bottom split offsets persist (scale-1 px); the default bottom split gives the timeline ≥ 40 % of the center column, bottom tabs minimum 180 px |
| Themes | `ToolTheme`/`ToolPalette` (`Core`); editor settings `dirge/theme` (Editor, Dirge Dark, Dirge Light, High Contrast, Custom), `dirge/theme_background`, `dirge/theme_foreground`, `dirge/theme_accent`; **View › Theme** cycles | — | Done | one palette for every document (written as inline custom properties on each document root; `theme.css` holds the Dirge Dark defaults and pane stylesheets use variables only) and every canvas (timeline, curve, panner, profiler graph and recording). Editor preset derives from the Godot editor theme (`base_color`, `dark_color_*`, `font_color`, `accent_color`, `error/warning/success_color`) so light editor themes get a light Dirge. Derived palettes keep text ≥ 4.5:1 and accent/semantic colors ≥ 3:1 against the background; a Custom pair below the floor is corrected and warned about once. Re-applied live on `EditorSettings.SettingsChanged` (deferred, so editor theme rebuilds are seen) |
| Tool UI localization | — | P2 | Planned | |

## Runtime API reference

Namespaces: `Dirge.Driver` (`DirgeServer`, `DirgeStats`), `Dirge.Runtime` (model runtime), `Dirge.Project` (authored data). `Attributes3D(Position, Velocity, Forward, Up)` uses `System.Numerics.Vector3`.

#### Explicit spatial worlds (source implementation; runtime qualification required)

`SpatialRegionId(int Value)` identifies a coordinate/world domain, not a seat or mixer. `default`/zero keeps the
legacy host world. The four-vector `Attributes3D` constructor and deconstruction remain unchanged; use
`attributes with { Region = region }` or the additional five-argument constructor. Existing `VoicePlay`,
`VoiceSet3D`, `Set3DAttributes` and `SetListener` signatures are unchanged and carry the region through attributes.
This is **source compatibility**, not a binary ABI guarantee: rebuild managed consumers of the enlarged value type.
There are no native ABI or precision changes.

On the scene-tree/main thread, call
`server.Driver.RegisterSpatialRegion(region, Node3D host, AudioListener3D listener)` for each nonzero region.
The host and current listener must be live in the **same audio-enabled viewport**, with a current `Camera3D` in
the same `World3D`. Godot's native spatial player enumerates world cameras before choosing each viewport's listener;
a listener without a camera is insufficient. Each registered region must use a distinct world, separate from the
default host world. Registration never creates listeners, rebuilds buses, or duplicates the mixer.
Use one native listener context per region; extra cameras/audio-enabled viewports sharing that world retain Godot's
native multi-listener maximum-volume semantics, not the model's nearest-listener selection or a weighted seat mix.
Do not share a region's World3D with an unrelated view/world. Start each machine-audible event once, not per seat.

Set the same region on the event's attributes and its model listener (`server.System.SetListener`); keep that model
listener's transform synchronized with the native listener. Positive weight enables a model listener; zero disables it.
The model never searches another region. Without an eligible listener, nondefault-region distance is maximally far
(clamped to the authored parameter maximum), normalized distance is 1, relative angles/speed are 0, and absolute
speed remains the emitter's own speed. Furthest stealing treats it as infinite distance. Legacy region zero retains
its historical listener-zero fallback only when that listener also belongs to region zero.

Call `UnregisterSpatialRegion(region)` **before** changing/freeing a world or withdrawing a view. It finishes its active
voices, clears stream/transform/region ownership, and returns players to the host pool. Also disable that region's model
listeners and stop/release its event instances (particularly persistent/scatter events); model listeners intentionally
remain engine-free and are not mutated by driver registration. A host/listener tree exit synchronously invalidates the
route and retires its native slots instead of reparenting while the tree is busy. Invalid/replaced worlds, disabled
audio viewports, and loss of a current listener/camera are withdrawn before the next command batch (including an empty
batch). Custom driver users must continue applying batches while idle. Re-register explicitly after withdrawal.
Unknown nonzero spatial regions finish without fallback playback, including live attribute changes.
Ordinary stop, natural finish, asset failure, and explicit withdrawal clear prior ownership before pool reuse.
Do not reparent `PlayerOf` results yourself.

Spatial attribute region changes move ordinary voices through the driver, preserving playback position and pause state;
native reparent/restart is not promised sample-continuous. Sync groups only combine voices in the same region. Moving one
sync layer to a different region splits that layer out; remaining layers stay in their world (cross-world layers no longer
share sample-lock). Nonspatial/UI/music voices ignore region identity and remain shared under the default host.
Recordings store integer `region` separately from the twelve coordinate floats; absent region loads as zero and default
command text is unchanged. Replay reproduces model state, not engine host registration: a driver replay needs matching
live region registrations supplied by the application. Audible-event/rewind deduplication remains application policy.

Coverage to run: `SpatialRegionTests`, `RecordingTests`, and the demo `--smoke` path, which includes real native world
membership, finish, withdrawal, migration, sync isolation and pool-reuse assertions. These are not evidence of audible
speaker isolation, mixing quality or multi-world performance until exercised and measured in the target runtime.


### `DirgeServer` (`Node`, autoload)

Exports: `ProjectPath` (file, `*.json`; empty = read `dirge/project_path`), `Seed`, `LiveUpdatePort` (0 = off), `MeterLoudness` (default true). Command line after `--`: `--dirge-live[=port]`, `--dirge-verbose`.

| Member | Description |
|---|---|
| `const string Version` | `"1.0.0"` |
| `const string ProjectPathSetting` | `"dirge/project_path"` |
| `event Action<IReadOnlyList<Command>> CommandsEmitted` | after each `Update`, before the commands are applied |
| `event Action<Feedback> FeedbackSent` | every driver → model feedback |
| `event Action<DirgeSystem> Loaded` | after `Load`/`HotReload` created a system |
| `const CallbackMask SignalMask` | callbacks handle-based instances forward as signals (everything but `SoundStopped`) |
| signals `EventStarted(int handle)`, `EventStopped(int handle)`, `TimelineMarker(int handle, string name, double position)`, `TimelineBeat(int handle, int bar, int beat, float bpm, double position)`, `SoundPlayed(int handle, string asset)`, `EventVirtualized(int handle)`, `EventUnvirtualized(int handle)`, `EventStolen(int handle)`, `ProgrammerSound(int handle, string key)` | emitted synchronously from inside `Update` for every instance started through `StartEvent` (also restored `HotReload` handles) |
| `bool IsLoaded` | a project is loaded |
| `DirgeSystem System` | the running system; throws when none is loaded |
| `AudioDriver Driver` | the `AudioServer` driver; throws before `_Ready` |
| `AssetCatalog Assets` | asset length/stream catalog |
| `BankLoader Banks` | bank preloading |
| `LiveUpdateHost? Live` | live-update host when enabled |
| `LoudnessCapture? Loudness` | Master loudness meter when `MeterLoudness` |
| `double LastUpdateMs` | duration of the last model update |
| `string Platform` | platform key for `PlatformOverrides` |
| `string Locale` | `TranslationServer.GetLocale()` |
| `IReadOnlyDictionary<int, EventInstance> Handles` | handle → instance for handle-started events |
| `DirgeStats Stats` | counters and loudness snapshot |
| `void EnableLiveUpdate(int port)` / `void DisableLiveUpdate()` | start/move or stop the live host |
| `void Unload()` | stop everything and drop the system |
| `void LoadFile(string path)` | parse a `res://`/`user://` JSON file and `Load` it |
| `void Load(AudioProject project)` | replace the project: rebuilds the bus layout and the system |
| `void HotReload(AudioProject project)` | live replace keeping globals, running snapshots and handles |
| `bool LiveSetBusVolume(string bus, float volumeDb)` | absolute authored fader override |
| `bool LiveSetEffectParameter(string bus, int effectIndex, string name, float value)` | write an effect property to the engine |
| `int StartEvent(string path)` | start + auto-release; 0 if unknown |
| `void StopEvent(int handle, bool immediate = false)` | |
| `void StopAllEvents(bool immediate = false)` | |
| `void SetParameter(int handle, string name, float value)` / `float GetParameter(int handle, string name)` | local first, then global |
| `void SetGlobalParameter(string name, float value)` / `float GetGlobalParameter(string name)` | |
| `void StartSnapshot(string name)` / `void StopSnapshot(string name)` / `bool IsSnapshotActive(string name)` | throws `KeyNotFoundException` for unknown names |
| `bool IsEventPlaying(int handle)` | |
| `EventInstance? GetInstance(int handle)` | null once stopped |
| `void SetCallbackMask(int handle, int mask)` | restrict a handle's signals; `mask` is a `CallbackMask` value, clipped to `SignalMask` |
| `void SetProgrammerSound(int handle, string assetPath)` | answer a `ProgrammerSound` emission (synchronously, inside the handler) and/or set the handle's fallback programmer asset |
| `bool LoadBank(string name)` / `void UnloadBank(string name)` / `bool IsBankLoaded(string name)` | |
| `string GetPlatform()` / `string GetLocale()` | GDScript-friendly accessors |
| `Godot.Collections.Dictionary GetStats()` | keys `time`, `instances`, `real_voices`, `update_ms`, `handles`, `momentary_lufs`, `short_term_lufs`, `integrated_lufs`, `true_peak_dbtp` |

`DirgeStats` record: `Time`, `Instances`, `RealVoices`, `LastUpdateMs`, `Handles`, `MomentaryLufs`, `ShortTermLufs`, `IntegratedLufs`, `TruePeakDbtp` (loudness is −∞ while only silence was heard).

### `DirgeSystem`

| Member | Description |
|---|---|
| `const int MaxListeners` | 8 |
| `DirgeSystem(AudioProject project, int seed = 0, IAssetCatalog? assets = null, string? platform = null)` | |
| `AudioProject Project` | |
| `MixerState Mixer` | |
| `ParameterBank GlobalParameters` | |
| `CommandBuffer Commands` | filled by `Update`; the driver clears it |
| `double Time` | seconds, sum of `dt` |
| `int InstanceCount` | live instances including nested |
| `int RealVoiceCount` | |
| `IReadOnlyList<EventInstance> Instances` | top-level live instances |
| `int Seed` | |
| `string? Platform` | |
| `int MaxRealVoices` | global real-voice cap |
| `string Locale` | audio-table locale; `""` = first authored |
| `EventInstance? Conductor` | playing instance of the latest `IsConductor` event |
| `IApiSink? Recorder` | profiler recording hook |
| `bool TryGetEvent(string path, out EventDescription description)` | |
| `EventDescription GetEvent(string path)` / `EventDescription GetEvent(DirgeId id)` | throw `KeyNotFoundException` |
| `void SetGlobalParameter(string name, float value, bool immediate = false)` / `float GetGlobalParameter(string name)` | |
| `void SetListener(int index, Attributes3D attributes, float weight = 1f)` | index 0..7; weight 0 disables |
| `void StopAll(StopMode mode)` | |
| `void Feedback(Feedback feedback)` | driver → model, applied next `Update` |
| `void Update(double dt)` | one tick |

### `EventDescription`

| Member | Description |
|---|---|
| `AudioEvent Event`, `DirgeId Id`, `string Path`, `bool Is3D` | |
| `bool IsOneshot` | not persistent, no loop region, no looping instrument |
| `double Length` | timeline length in seconds |
| `IReadOnlyList<ParameterDefinition> Parameters` | local parameters |
| `int InstanceCount` | |
| `EventInstance CreateInstance()` | pooled |

### `EventInstance`

Enums: `StopMode { AllowFadeout, Immediate }`, `PlaybackState { Starting, Playing, Sustaining, Stopping, Stopped }`, `CallbackMask` flags (`Started`, `Stopped`, `TimelineMarker`, `TimelineBeat`, `SoundPlayed`, `SoundStopped`, `Virtualized`, `Unvirtualized`, `Stolen`, `ProgrammerSound`, `All`). Callback payloads: `StartedCallback`, `StoppedCallback`, `MarkerCallback(Name, Position)`, `BeatCallback(Bar, Beat, Bpm, Position)`, `SoundPlayedCallback(Voice, Asset)`, `SoundStoppedCallback(Voice)`, `VirtualizedCallback`, `UnvirtualizedCallback`, `StolenCallback`, `ProgrammerSoundCallback(Key)` with settable `Asset`.

| Member | Description |
|---|---|
| `EventDescription Description` | |
| `PlaybackState State` | |
| `bool IsVirtual` | |
| `CallbackMask CallbackMask` | which callbacks are raised (default `All`) |
| `event Action<EventInstance, CallbackEvent> Callback` | |
| `double TimelinePosition` | seconds; setting seeks at the next update, magnet regions snap |
| `bool IsInTransition` / `double TransitionPosition` | transition timeline state |
| `void Start()` | honours max instances, stealing, cooldown, start quantization |
| `void Stop(StopMode mode)` | |
| `void KeyOff()` | release a sustain point and begin AHDSR releases |
| `void SetPaused(bool paused)` | |
| `void SetParameter(string name, float value, bool immediate = false)` / `float GetParameter(string name)` | local first, then global |
| `float GetParameterVelocity(string name)` | units per second |
| `void SetParameterByLabel(string name, string label)` | |
| `void Set3DAttributes(Attributes3D attributes)` | |
| `void SetVolume(float db)` / `void SetPitch(float ratio)` | per-instance offsets |
| `void SetProgrammerSound(AssetRef asset)` | fallback asset for programmer instruments |
| `void Release()` | destroy when stopped |

### `MixerState`

| Member | Description |
|---|---|
| `MixerState(AudioProject project, ParameterBank globals, CommandBuffer commands, DirgeRandom random, string? platform = null)` | constructed by `DirgeSystem` |
| `int BusCount` / `int ActiveSnapshotCount` | |
| `string BusName(DirgeId bus)` | |
| `bool TryGetBus(string name, out DirgeId id)` / `bool TryGetVca(string name, out DirgeId id)` | |
| `float EffectiveBusVolumeDb(DirgeId bus)` / `float EffectiveBusPitch(DirgeId bus)` / `float EffectiveVcaVolumeDb(DirgeId vca)` | values as of the last `Update` |
| `void SetBusVolume(string bus, float db)` | runtime offset on top of the evaluated volume |
| `void SetBusMute(string bus, bool mute)` / `void SetBusSolo(string bus, bool solo)` | |
| `void SetVcaVolume(string vca, float db)` | runtime offset |
| `bool HasSnapshot(string name)` | |
| `SnapshotInstance StartSnapshot(string name)` | several instances may run; throws for unknown names |
| `void AddAutomation(AutomationLane lane)` / `bool RemoveAutomation(DirgeId laneId)` | project-level mixer automation from global parameters |
| `void Update(double dt)` | called by `DirgeSystem.Update` |

### `SnapshotInstance`

| Member | Description |
|---|---|
| `Snapshot Snapshot` | authored definition |
| `float Intensity` | 0..1 scale multiplied with the AHDSR envelope |
| `bool IsActive` | |
| `float EffectiveIntensity` | scalar × envelope |
| `void Stop()` | begin the release; removed by the mixer when finished |

## Commands (the T1 oracle)

`DirgeSystem.Update` fills `Commands` with `VoicePlay`, `VoiceStop`, `VoiceSeek`, `VoiceSetVolume`, `VoiceSetPitch`, `VoiceSet3D`, `VoiceSetPaused`, `BusSetVolume`, `BusSetMute`, `BusSetSolo`, `BusSetBypass`, `EffectSetEnabled`, `EffectSetParameter`. `AudioDriver` applies them and reports `VoiceFinished`/`VoiceFailed` feedback. A `Recorder` captures API calls and commands into a `Recording`; `Replayer.Replay` re-runs it on a fresh system and `Replayer.Compare` returns the first divergence.
