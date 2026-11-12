# Dirge capabilities and runtime API

Feature matrix from [`SPEC.md`](SPEC.md) §3, followed by the 1.1.0 GDScript runtime contract. Matrix statuses retain the existing feature scope: **Done** — implemented in the previous baseline and required by the cutover; **Partial** — implemented with a documented gap; **Planned** — not implemented. These labels are not evidence of fresh 1.1.0 test, UI or platform qualification. The matrix's PascalCase type/property names identify schema concepts from the original model; they are not C# APIs to import in 1.1.0. Authored keys remain camelCase; lower-level runtime members are snake_case.

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
| Callbacks: started / stopped / marker / beat / sound played / sound stopped / virtualized / unvirtualized / stolen / programmer sound | instance `callback(instance, event: Dictionary)` + integer callback mask; public `DirgeServer` signals | P0 | Done | handle-based instances emit `EventStarted`, `EventStopped`, `TimelineMarker`, `TimelineBeat`, `SoundPlayed`, `EventVirtualized`, `EventUnvirtualized`, `EventStolen`, `ProgrammerSound`; `SoundStopped` is available through the lower-level instance callback |
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
| Built-ins: distance, distance normalized, direction, elevation, cone angle, orientation, speed, speed absolute | `BuiltInParameter` | P0 | Done | from the nearest weighted listener |
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

The earlier responsiveness measurements were collected with companion NecronomiDOM style-reuse changes in the maintained Eldritch editor, not the stock-Godot cutover environment. They do not qualify 1.1.0. The HTML/CSS UI remains on Neo's native GDExtension; the required GDScript selector/mutation/scroll bridge is newer than released 1.1.5. `dependency-lock.json` pins source `99fa1910866590cfaa28f6c56489a5711bc8fddf` and the exact local diagnostic package hash. Only Linux x64 is included; Linux ARM64, Windows x64 and both macOS builds remain required prerequisites, not dropped support. This is not a published release identity. Bridge DOM/input/scroll/lifetime checks passed stock non-.NET Godot 4.7.2; integrated Dirge qualification requires the exact installed-package receipt and inspected screenshots. See [package details and outstanding compiler advisory](../README.md#building-from-source). Consuming Neo's precompiled NativeAOT binary needs no .NET installation.

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
| Scripting | GDScript model objects and Dictionaries; retained PascalCase `DirgeServer` API | P0 | Done | no typed C# assembly dependency |
| Undo/redo, multi-select, copy/paste across events | `ProjectDocument`, `ToolCommand` | P0 | Done | clipboard is pane-local and survives event switches |
| Localization audio tables | `AudioTable`, `DirgeSystem.Locale` | P1 | Done | |
| Banks | `AudioBank`; `DirgeServer.LoadBank/UnloadBank/IsBankLoaded`, `BankLoader` | — | Done | preload groups over `ResourceLoader`; no bank files |
| Version control | text `*.dirge.json` | — | Done | |
| Hi-DPI / editor scale | `ToolScale` (`Core`); `dirge/ui_scale` editor setting | — | Done | one scale for every document host and canvas: editor display scale × `main_font_size` / 14, or `dirge/ui_scale` verbatim when > 0 (0 = auto); re-applied live on settings/DPI changes. Hosts mount through `ToolDocuments.Slot` — never as direct children of a Godot container, which resets their scale |
| Layout preferences | `ToolSettings` (`Core`): `dirge/header_sections`, `dirge/split_browser`, `dirge/split_bottom` | — | Done | event header sections start collapsed and persist; browser/center and top/bottom split offsets persist (scale-1 px); the default bottom split gives the timeline ≥ 40 % of the center column, bottom tabs minimum 180 px |
| Themes | `ToolTheme`/`ToolPalette` (`Core`); editor settings `dirge/theme` (Editor, Dirge Dark, Dirge Light, High Contrast, Custom), `dirge/theme_background`, `dirge/theme_foreground`, `dirge/theme_accent`; **View › Theme** cycles | — | Done | one palette for every document (written as inline custom properties on each document root; `theme.css` holds the Dirge Dark defaults and pane stylesheets use variables only) and every canvas (timeline, curve, panner, profiler graph and recording). Editor preset derives from the Godot editor theme (`base_color`, `dark_color_*`, `font_color`, `accent_color`, `error/warning/success_color`) so light editor themes get a light Dirge. Derived palettes keep text ≥ 4.5:1 and accent/semantic colors ≥ 3:1 against the background; a Custom pair below the floor is corrected and warned about once. Re-applied live on `EditorSettings.SettingsChanged` (deferred, so editor theme rebuilds are seen) |
| Tool UI localization | — | P2 | Planned | |

## Migration from 1.0 C# consumers

1. Replace Dirge's old addon files rather than layering `.gd` files over obsolete `.cs` files. Remove the `Dirge.targets` and `NecronomiDom.targets` imports used for this integration, obsolete Dirge assembly references and typed `using Dirge.*` consumers. Keep unrelated C# code/tooling if your game uses it; Dirge itself no longer requires it.
2. Install the matching native Neo bridge package, preserving its `.gdextension`, binaries and notices. Do **not** remove Neo or replace its HTML/CSS UI with Controls. Released 1.1.5 is insufficient; use the exact local diagnostic lock above, without treating it as a published cross-platform release.
3. Existing handle-based GDScript calls and server signals keep **PascalCase**. Replace typed C# instance/model calls with the snake_case GDScript APIs below. There are no compatibility assemblies or obsolete-name aliases.
4. Authored objects become `Dictionary`/`Array`: `AudioProject.Events` → `project["events"]`, `AudioEvent.LocalParameters` → `event["localParameters"]`, `Bus.VolumeDb` → `bus["volumeDb"]`. IDs stay 32 hexadecimal characters, assets stay paths, enums stay strings, and polymorphic objects keep `$type` (for example `"Clip"`, not a script class name). Do not snake_case persisted keys.
5. Runtime instances remain objects, not serialized project Dictionaries. Stop modes are `"AllowFadeout"`/`"Immediate"`; playback states are string values. Callback masks remain integer bits. Replace typed callback records with Dictionaries containing `type` and camelCase payload fields.
6. Use Godot `Vector3` values in runtime 3D attribute Dictionaries (`position`, `velocity`, `forward`, `up`) instead of `System.Numerics.Vector3`/`Attributes3D` constructors. Godot forward is −Z.
7. Use model constructors and serializers rather than hand-renaming JSON fields. Deep-copy mutable authored data with `duplicate(true)` when isolating edits. Editor changes go through `ProjectDocument.transact(label, callable)`, then undo/redo; direct mutations alone do not publish document changes.

```gdscript
const Model = preload("res://addons/dirge/model/Project/project_model.gd")
const ProjectJson = preload("res://addons/dirge/model/Serialization/project_json.gd")
const Validator = preload("res://addons/dirge/model/Validation/project_validator.gd")

var project = Model.new_project("Example")
var master = Model.create("Bus", {"name": "Master"})
project["mixer"]["buses"].append(master)
var event = Model.create("AudioEvent", {"path": "Ambience/Wind", "bus": master["id"]})
project["events"].append(event)
var diagnostics = Validator.validate(project) # code/message/severity/subject dictionaries
var text = ProjectJson.serialize(project)
var restored = ProjectJson.deserialize(text)
```

`Model.defaults(kind)`, `create(kind, values)`, `hydrate(values, kind)`, `new_id()` and `property_ref(kind, owner, effect_index, name)` replace typed constructors. `new_project` does not add a Master bus automatically; the editor's New action does.
`ProjectJson.load_file(path)` returns project data; `save_file(path, project)` returns a Godot `Error`. Serialization retains the existing JSON schema, unknown authored fields and deterministic ordering; no `.tres` conversion or data migration is required.
Regression fixtures and command recordings remain JSON. A language/RNG change is not a promise that old recordings are bit-identical; replay and compare them during qualification.

## Runtime API reference

Names below describe the GDScript contract. Explicit `preload("res://addons/dirge/model/Runtime/dirge_system.gd")` replaces CLR namespace imports when constructing a standalone model. Use the server-owned system for normal game playback so its driver applies commands.

### `DirgeServer` (`Node`, autoload)

Exports: `ProjectPath` (file, `*.json`; empty = read `dirge/project_path`), `Seed`, `LiveUpdatePort` (0 = off), `MeterLoudness` (default true). Command line after `--`: `--dirge-live[=port]`, `--dirge-verbose`.

| Member | Description |
|---|---|
| `Version`, `ProjectPathSetting` | addon version (`1.1.0`) and `"dirge/project_path"` |
| `CommandsEmitted(commands)`, `FeedbackSent(feedback)`, `Loaded(system)` | signals after model update/before apply, for driver feedback, and after load/hot reload |
| `SignalMask` | integer mask for forwarded callbacks (excludes `SoundStopped`) |
| `EventStarted(handle)`, `EventStopped(handle)` | lifecycle signals for handle-started instances |
| `TimelineMarker(handle, name, position)`, `TimelineBeat(handle, bar, beat, bpm, position)` | timeline signals |
| `SoundPlayed(handle, asset)` | sound-start signal |
| `EventVirtualized(handle)`, `EventUnvirtualized(handle)`, `EventStolen(handle)` | voice lifecycle signals |
| `ProgrammerSound(handle, key)` | synchronously answer with `SetProgrammerSound` |
| `IsLoaded`, `System`, `Driver`, `Assets`, `Banks`, `Live`, `Loudness` | loaded state and runtime providers; guard `IsLoaded` before using the model |
| `LastUpdateMs`, `Platform`, `Locale`, `Handles`, `Stats` | update duration, platform/locale, handle→instance Dictionary, stats snapshot |
| `EnableLiveUpdate(port)`, `DisableLiveUpdate()` | start/move or stop localhost host |
| `Unload()`, `LoadFile(path)` | stop/drop the system; load JSON |
| `Load(project: Dictionary)`, `HotReload(project: Dictionary)` | replace the model/bus layout; hot reload preserves globals, snapshots and handles |
| `LiveSetBusVolume(bus, volumeDb)`, `LiveSetEffectParameter(bus, effectIndex, name, value)` | live fader/effect edits; return success |
| `StartEvent(path) -> int` | start and auto-release; 0 if unknown |
| `StopEvent(handle, immediate=false)`, `StopAllEvents(immediate=false)` | stop handles/events |
| `SetParameter(handle, name, value)`, `GetParameter(handle, name)` | local first, then global |
| `SetGlobalParameter(name, value)`, `GetGlobalParameter(name)` | global bank |
| `StartSnapshot(name)`, `StopSnapshot(name)`, `IsSnapshotActive(name)` | snapshot lifecycle |
| `IsEventPlaying(handle)`, `GetInstance(handle)` | state and lower-level instance (null when absent) |
| `SetCallbackMask(handle, mask)` | clips integer bits to `SignalMask` |
| `SetProgrammerSound(handle, assetPath)` | synchronous programmer response or fallback asset |
| `LoadBank(name)`, `UnloadBank(name)`, `IsBankLoaded(name)` | preload groups |
| `GetPlatform()`, `GetLocale()`, `GetStats()` | convenience accessors |

`GetStats()` returns Dictionary keys `time`, `instances`, `real_voices`, `update_ms`, `handles`, `momentary_lufs`, `short_term_lufs`, `integrated_lufs`, `true_peak_dbtp`; silence has −∞ loudness.

### `DirgeSystem`

| Member | Description |
|---|---|
| `.new(project, seed=0, assets=null, platform="")` | project Dictionary, optional catalog/platform |
| `project`, `mixer`, `global_parameters`, `commands`, `time`, `instances` | authored graph, evaluated state, command Array and clock |
| `seed`, `platform`, `locale`, `max_real_voices`, `conductor` | playback configuration and conductor instance |
| `instance_count`, `real_voice_count`, `recorder` | live counters and optional profiler recording hook |
| `get_event(path_or_id)` | description or null; replace `TryGetEvent`/throwing C# lookups with a null check |
| `set_global_parameter(name, value, immediate=false)`, `get_global_parameter(name)` | global control |
| `set_listener(index, attributes, weight=1.0)` | up to eight listeners; weight 0 disables |
| `stop_all(mode="AllowFadeout")` | stop live events |
| `feedback(event: Dictionary)` | driver feedback |
| `update(dt)` | one model tick; driver applies then clears `commands` |

### `EventDescription`

| Member | Description |
|---|---|
| `event`, `id`, `path`, `is_3d`, `is_oneshot` | authored Dictionary, identity and traits |
| `length`, `parameters`, `instance_count` | seconds, local definitions and live count |
| `create_instance()` | instantiate for explicit lifecycle control |

### `EventInstance`

| Member | Description |
|---|---|
| `description`, `state`, `is_virtual`, `callback_mask` | description, string playback state and callback filtering |
| `callback(instance, event)` | signal with Dictionary callback payload |
| `timeline_position` | seconds; seeking observes timeline/magnet logic |
| `is_in_transition`, `transition_position` | transition timeline state |
| `start()`, `stop(mode="AllowFadeout")`, `release()` | honours caps, stealing, cooldown, quantization; release destroys when stopped |
| `key_off()`, `set_paused(paused)` | sustain/AHDSR release; pause |
| `set_parameter(name, value, immediate=false)`, `get_parameter(name)` | local first then global |
| `get_parameter_velocity(name)`, `set_parameter_by_label(name, label)` | units/second and labeled selection |
| `set_3d_attributes(attributes)` | Dictionary of Godot vectors |
| `set_volume(db)`, `set_pitch(ratio)` | per-instance offsets |
| `set_programmer_sound(asset)` | fallback path for programmer instruments |

States: `Starting`, `Playing`, `Sustaining`, `Stopping`, `Stopped`. Callback bits retain order: `Started`, `Stopped`, `TimelineMarker`, `TimelineBeat`, `SoundPlayed`, `SoundStopped`, `Virtualized`, `Unvirtualized`, `Stolen`, `ProgrammerSound` (bits 0–9; All = 1023).
Callback `type` values retain record names: `StartedCallback`, `StoppedCallback`, `MarkerCallback` (`name`, `position`), `BeatCallback` (`bar`, `beat`, `bpm`, `position`), `SoundPlayedCallback` (`voice`, `asset`), `SoundStoppedCallback` (`voice`), `VirtualizedCallback`, `UnvirtualizedCallback`, `StolenCallback`, `ProgrammerSoundCallback` (`key`, settable `asset`).

### `MixerState`

| Member | Description |
|---|---|
| `.new(project, globals, commands, random, platform="")` | normally constructed by `DirgeSystem`; shared `RandomNumberGenerator` |
| `bus_count`, `active_snapshot_count`, `active_snapshots` | mixer/snapshot state |
| `bus_name(id)` | bus display name |
| `effective_bus_volume_db(id)`, `effective_bus_pitch(id)`, `effective_vca_volume_db(id)` | values at last update |
| `set_bus_volume(bus, db)`, `set_bus_mute(bus, mute)`, `set_bus_solo(bus, solo)` | runtime overrides |
| `set_vca_volume(vca, db)` | runtime VCA offset |
| `has_snapshot(name)`, `start_snapshot(name)` | existence and instance creation; check existence for authored names |
| `add_automation(lane)`, `remove_automation(lane_id)` | global-parameter mixer automation |
| `update(dt)` | called by system |

`ParameterBank.get_value(id_or_name)` / `set_value(id_or_name, value, immediate=false)` replace typed parameter-bank access.

### `SnapshotInstance`

`snapshot` is the authored Dictionary; `intensity` is the 0..1 scalar multiplied with the AHDSR envelope.
`is_active` and `effective_intensity` expose state; `stop()` begins release and the mixer removes the finished instance.

## Commands (the T1 oracle)

`DirgeSystem.update` fills `commands` with Dictionaries whose `type` is `VoicePlay`, `VoiceStop`, `VoiceSeek`, `VoiceSetVolume`, `VoiceSetPitch`, `VoiceSet3D`, `VoiceSetPaused`, `BusSetVolume`, `BusSetMute`, `BusSetSolo`, `BusSetBypass`, `EffectSetEnabled`, or `EffectSetParameter`. Remaining fields retain camelCase command-field names. The audio driver applies commands and returns `VoiceFinished`/`VoiceFailed` feedback Dictionaries. Profiler recording captures API calls and commands; replay runs a fresh model and comparison reports divergence. `tests/gdscript/run.gd` is the headless regression entrypoint; `scripts/validate.sh` runs JSON validation. None of these descriptions asserts a passing cutover run.
