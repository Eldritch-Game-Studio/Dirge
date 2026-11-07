# Dirge — adaptive audio authoring for Godot

Named Dirge. Standalone MIT addon for stock Godot 4.7.2 .NET. UI via NecronomiDOM ≥ 1.1.4. Reference: FMOD Studio 2.03 feature set. Target: parity on everything a game team uses day to day, better on authoring ergonomics, testability and version control.

## 1. Principles

- **Pure model.** `Dirge.Model` has no Godot reference. Events, parameters, snapshots, mixer graph, evaluation and serialization are plain C#, unit-tested headless. Game code may reference it directly.
- **One DSP path.** Playback is Godot's `AudioServer`. The tool auditions through the same path the game ships. No second engine, no browser audio.
- **Resources are config, not state.** Authored data is Godot `Resource`s (`.tres`, text, diffable). Runtime state lives in C# objects owned by the runtime service.
- **Native only when Godot can't.** GDExtension effects are a separate optional package (`Dirge.Native`), added per concrete missing effect. The addon is fully usable without it.
- **Verification tiers.** Every feature below carries the cheapest tier that proves it: T0 model unit test · T1 headless Godot (`--headless`, `AudioServer` with Dummy driver, command log) · T2 offline render comparison (`AudioEffectCapture` → WAV → tolerance) · T3 interactive/manual.

## 2. Priority tiers

- **P0** — parity blocker; v1.0 ships with all P0.
- **P1** — parity; v1.x.
- **P2** — parity requiring native, or "better" features; after v1.
- **—** — deliberate non-goal.

## 3. Feature matrix

The **Status** column records the 1.0.0 implementation state: Done, Partial (with the gap), Planned. The same matrix with code references is in [`capabilities.md`](capabilities.md).

### 3.1 Events

| FMOD | Ours | P | Godot mechanism | Gap / note | Status |
|---|---|---|---|---|---|
| Event (timeline + parameter sheets) | `AudioEvent` resource: tracks, instruments, parameter sheets, macros | P0 | model → `AudioStreamPlayer` pool | — | Done |
| Event instance lifecycle (create/start/stop immediate·fadeout/release) | `EventInstance` API identical semantics | P0 | pool + fade via `volume_db` tween in model clock | — | Done |
| Nested / referenced events | `EventInstrument` referencing another `AudioEvent` | P0 | recursive instance | depth limit 8, cycle check at validate | Done |
| Event macros: priority, max instances, stealing (oldest / quietest / furthest / virtualize / none), cooldown, persistent, min distance / max distance, rolloff, doppler, schedule delay / lookahead | all | P0 | model scheduler; virtualize = instance keeps clock, releases player | lookahead maps to `AudioStreamPlayer.play(from)` at predicted mix time; doppler scale has no engine equivalent (on/off only) | Done |
| Voice stealing at global polyphony cap | global + per-bus voice caps | P0 | pool size + `AudioServer` output latency | — | Done |
| Timeline markers: loop region, transition marker, transition region, sustain point, destination marker, tempo marker, magnet region | all except magnet | P0 (magnet P1) | model clock drives `seek`; sample-accurate via `AudioStreamSynchronized` / `AudioStreamInteractive` where clip-level | Godot stream seek is block-quantized (~10 ms at default latency); sample-accurate stitching requires `AudioStreamInteractive` clips → see 3.3 | Done |
| Transition quantization (beat/bar/marker, with transition timeline) | yes | P0 | tempo markers + model clock | — | Done |
| Trigger conditions: parameter range, probability, labeled value | yes | P0 | model | — | Done |
| Async instruments, cut-off on condition exit | yes | P0 | model | — | Done |
| Callbacks: started/stopped/timeline marker/beat/sound played/virtualized/stolen | C# events + `Signal`-bridged GDScript | P0 | — | — | Done (C# events; `DirgeServer` signals for handle-based instances incl. `ProgrammerSound` + `SetProgrammerSound`) |
| Event preview/audition in tool with parameter scrubbing | yes | P0 | runtime in editor | — | Done |
| Per-event 3D attributes (position, velocity, forward/up) | yes | P0 | `AudioStreamPlayer3D` for spatial instances | — | Done |
| Event instance user data / property overrides (`setProperty`) | yes | P1 | — | — | Planned |

### 3.2 Instruments

| FMOD | Ours | P | Godot mechanism | Gap / note | Status |
|---|---|---|---|---|---|
| Single | `ClipInstrument` | P0 | `AudioStreamWAV`/`Ogg`/`MP3` | — | Done |
| Multi (random / sequential / shuffle, avoid-repeat N, per-entry weight) | `MultiInstrument` | P0 | `AudioStreamRandomizer` or model-side pick | model-side pick so weights/avoid-repeat are deterministic and testable | Done |
| Scatterer (spawn interval, min/max distance, polyphony, pitch/volume rand) | `ScattererInstrument` | P0 | model scheduler + 3D players | — | Done |
| Programmer (runtime-provided key → clip) | `ProgrammerInstrument` with resolver delegate | P0 | — | localization table resolver built in (3.9) | Done |
| Event / nested | 3.1 | P0 | | | Done |
| Command (start/stop another event, set parameter) | `CommandInstrument` | P0 | model | — | Done |
| Snapshot instrument (start/stop snapshot during region) | `SnapshotInstrument` | P0 | 3.5 | — | Done |
| Silence | `SilenceInstrument` | P0 | — | — | Done |
| Plugin instrument (synth) | `GeneratorInstrument` over `AudioStreamGenerator` | P2 | `AudioStreamGenerator` | C#-side generator; native synth optional | Planned |
| Instrument properties: volume, pitch, start offset, loop, fade in/out, cut/stop mode, polyphony, trigger delay, playback percentage | all | P0 | | | Done |
| Audio file: streaming vs memory, compression | per-asset import hints | P1 | Godot importer settings | tool writes `.import` overrides; no own codec | Planned |

### 3.3 Music / adaptive

| FMOD | Ours | P | Godot mechanism | Gap / note | Status |
|---|---|---|---|---|---|
| Beat-synced transitions | yes | P0 | tempo markers; `AudioStreamInteractive` for sample-accurate clip switches; model clock for everything else | **Better:** expose both; tool renders which clips are stitched natively | Partial (model clock only; no `AudioStreamInteractive`) |
| Multi-track vertical layering with per-layer parameter automation | yes | P0 | `AudioStreamSynchronized` (up to 32 streams) | — | Done |
| Stingers / one-shots quantized to music | yes | P0 | beat callbacks + lookahead | — | Done |
| Tempo map (multiple tempos, time signatures) | yes | P0 | model | — | Done |
| Beat/bar callbacks | yes | P0 | model clock | — | Done |
| Playlist (ordered clips, loop/shuffle) | yes | P0 | `AudioStreamPlaylist` | — | Partial (`MultiInstrument` sequential/shuffle) |

### 3.4 Parameters

| FMOD | Ours | P | Note | Status |
|---|---|---|---|---|
| Continuous, discrete, labeled | yes | P0 | | Done |
| Local (per instance) and global | yes | P0 | | Done |
| Seek speed (asymmetric up/down), hold | yes | P0 | | Done |
| Velocity (rate-of-change driven) | yes | P1 | | Done |
| Built-ins: distance, distance normalized, direction, elevation, event cone angle, event orientation, speed, speed absolute | yes | P0 | computed from listener + 3D attributes each tick | Done |
| Automation of any property by any parameter (curves: linear, smooth, stepped, custom points) | yes | P0 | curve evaluated in model; applied to `volume_db`, `pitch_scale`, effect params via `AudioServer.set_bus_effect`/`AudioEffect` setters | Done |
| Modulators: random, AHDSR, sidechain, autopitch, LFO (sine/square/tri/saw/noise, rate, phase, sync to tempo) | all | P0 (sidechain P2) | sidechain needs level data from another bus → `AudioEffectCapture` RMS in C# (P1, ~1 block latency) or native (P2); autopitch = piecewise-linear pitch ratio through (min, pitch-at-min), (reference, 1), (max, pitch-at-max); LFO/Random also on mixer properties (`AudioProject.MixerModulators`) | Done (random/AHDSR/LFO/autopitch; sidechain P2 Planned) |
| Parameter conditions on instruments, transitions, logic markers | yes | P0 | | Done |

### 3.5 Mixer

| FMOD | Ours | P | Godot mechanism | Gap / note | Status |
|---|---|---|---|---|---|
| Buses hierarchy, master | yes | P0 | `AudioServer` buses | Godot bus = single parent send; hierarchy matches | Done |
| Group bus properties: volume, pitch, mute, solo, bypass | yes | P0 | `set_bus_volume_db/mute/solo/bypass_effects`; pitch via `AudioEffectPitchShift` | pitch on bus costs a pitch-shift effect; tool warns | Done |
| Return buses + sends (pre/post fader) | yes | P2 | **no aux sends in Godot** | native `AudioEffectSend` + `AudioEffectReturn` pair in `Dirge.Native`. Without native: tool offers "duplicate-route" (instrument plays a second player into the return bus; not sample-locked, no pre/post) — labeled degraded | Done (native only; no degraded fallback) |
| VCAs | yes | P0 | model-side gain summation onto bus volume | — | Done |
| Effects: 3-EQ, multiband EQ, compressor, limiter, gain, delay, reverb, chorus, flanger, distortion, pitch shifter, tremolo, transceiver, convolution reverb, loudness meter, spectrum | EQ6/10/21, Compressor, Limiter/HardLimiter, Amplify, Delay, Reverb, Chorus, Phaser (flanger-equivalent), Distortion, PitchShift, StereoEnhance, Panner, Filter family, Capture, SpectrumAnalyzer | P0 | Godot built-ins | tremolo = mixer LFO modulator on bus volume (strip `LFO` button) or on any effect parameter; transceiver (P2 native; = send/return), convolution reverb (P2 native), LUFS meter (P1, C# over `AudioEffectCapture`) | Done (tremolo via mixer LFO; transceiver/convolution need `Dirge.Native`) |
| Pre/post fader effect position | yes | P0 | effect order + Amplify as "fader" when pre-fader effects needed | Godot applies bus volume after effects; `Bus.FaderIndex` < chain length inserts an `AudioEffectAmplify` fader at the split point, bus volume held at 0 dB, `BusSetVolume` drives the amplify; driver maps authored → engine indices | Done |
| Snapshots: blending by intensity, priority ordering, overriding vs blending, scoped properties, AHDSR on intensity | yes | P0 | model computes final bus/effect values each tick | **Better:** snapshot diff view in tool | Done |
| Sidechain compressor | yes | P0 | `AudioEffectCompressor.sidechain` (bus name) | native Godot feature | Done |
| Ducking presets | yes | P0 | sidechain compressor template | add-effect popup → source bus picker → compressor keyed from it | Done |
| Spatializer / object spatializer / HRTF | Godot 3D attenuation + panning strength | P0 (HRTF —) | `AudioStreamPlayer3D`, `AudioListener3D` | no HRTF in Godot; non-goal | Done |
| Rolloff: linear, linear squared, inverse, inverse tapered, custom curve | Godot: inverse, inverse square, logarithmic, disabled; custom via automation on distance | P0 | `EventMacros.Rolloff` → `attenuation_model` | custom curve via built-in `distance` parameter → volume automation with `Rolloff.Disabled` | Done (engine models; custom via `Distance` automation) |
| Doppler | yes | P0 | `doppler_tracking` (physics step) | engine has no doppler scale: `DopplerScale` > 0 enables, 0 disables | Done (on/off; scale authored only) |
| Occlusion/obstruction | parameter-driven (game supplies) + lowpass automation template | P1 | | FMOD also delegates to game geometry | Partial (parameter-driven; no template) |
| Per-platform mixer overrides | per-`OS.get_name()` override sets | P1 | | | Done |
| Loudness metering LUFS-M/S/I, true peak | yes | P1 | C# ITU-R BS.1770 over `AudioEffectCapture` on master | **Better than Godot**; parity with FMOD | Done |
| Peak/RMS meters on every bus | yes | P0 | `get_bus_peak_volume_left/right_db` | | Done |

### 3.6 Tooling (NecronomiDOM)

| FMOD Studio | Ours | P | Note | Status |
|---|---|---|---|---|
| Event editor: timeline, tracks, instruments, parameter sheets, logic markers, automation lanes | yes | P0 | editor dock; HTML/CSS; drag/drop; zoom | Done |
| Mixer view: strips, meters, routing, effect chain, snapshots | yes | P0 | | Done |
| Browser/assets panel with tags, search | yes | P0 | tag chips filter the events tree; tags edited on the selected event | Done |
| Transport: play/stop/loop, parameter scrub, 3D preview panner | yes | P0 | 3D preview = listener/emitter in a canvas, drives built-in params | Done |
| Profiler: live instance list, voice count, CPU, per-bus levels, event timeline recording, playback of recording | live view P0, record/replay P1 | | **Better:** recording is JSON; diffable; feeds T1 tests | Done |
| Live Update (connect to running game) | yes | P1 | runtime hosts a localhost TCP/WebSocket; tool edits apply live; same document also mountable in-game as overlay | | Done |
| Sandbox (audition event in a 3D scene) | yes | P1 | demo scene + in-game overlay | Done |
| Validation panel (missing assets, cycles, unreachable instruments, unassigned parameters) | yes | P0 | **Better:** also runs as CLI `--headless` for CI | Done |
| Scripting (FMOD: JavaScript) | C# scripting via API + GDScript | P0 | editor API surface = runtime API | Done |
| Undo/redo, multi-select, copy/paste across events | yes | P0 | | Done |
| Localization audio tables | yes | P1 | key × locale → clip; programmer instrument resolver | Done |
| Banks: build, per-platform, streaming assets, bank loading API | **—** | — | Godot PCK + `ResourceLoader`; "bank" = a folder/`AudioBank` resource listing events for preload. Provide `LoadBank/UnloadBank` that preloads streams. | Done (`LoadBank`/`UnloadBank` preload) |
| Version control integration | **—** (text resources + git) | — | **Better:** no binary project file | Done |
| Localization of tool UI | P2 | | | Planned |

### 3.7 Runtime API (parity with FMOD Studio API)

`Dirge.Runtime` C# + GDScript bridge (`DirgeServer` autoload):

- `System`: `Load(AudioProject)`, `LoadBank/UnloadBank`, `GetEvent(path|guid)`, `SetGlobalParameter`, `GetBus/GetVca/GetSnapshot`, `SetListenerAttributes(index, Transform3D, velocity)`, up to 8 listeners with weights, `Update()` driven by `_process` or manual, `FlushCommands`.
- `EventDescription`: `CreateInstance`, `Is3D`, `IsOneshot`, `GetLength`, `GetParameterDescriptions`, `LoadSampleData/Unload`.
- `EventInstance`: `Start`, `Stop(mode)`, `Pause`, `Set/GetParameter(by name|id)`, `Set3DAttributes`, `SetVolume/Pitch`, `GetPlaybackState`, `GetTimelinePosition/Set`, `SetCallback(mask)`, `Release`, `KeyOff`, `SetProperty`.
- `Bus`: volume, mute, solo, paused, `StopAllEvents`, `LockChannelGroup` equivalent = `GetGodotBusIndex`.
- `Snapshot`: `Start/Stop`, `SetIntensity`.
- `Vca`: volume.
- Command log: every API call recorded in a ring buffer (profiler + T1 oracle).
- Thread model: API callable from any thread; applied on main thread at `Update()`; `AudioServer` touched only there.

### 3.8 Performance targets

| Metric | Target |
|---|---|
| 256 concurrent instances, 64 real voices | `Update()` ≤ 0.5 ms on desktop |
| Allocation in `Update()` steady state | 0 bytes |
| Parameter set → audible | ≤ 1 mix block + `AudioServer.get_output_latency()` |
| Transition timing error (model-clock seek path) | ≤ 1 mix block; sample-accurate when `AudioStreamInteractive` path applies |

### 3.9 Better-than-FMOD (explicit)

- Text, diffable project; CI validation CLI; deterministic model (seeded RNG) → unit-testable mix logic.
- Profiler recordings as test fixtures.
- Same UI document runs in editor dock and in-game overlay (NecronomiDOM) — no separate "connect" build.
- Open MIT, no per-seat/per-title licensing.
- Godot-native resources: hot reload, `ResourceLoader` threading, PCK streaming already handled.

### 3.10 Non-goals

HRTF/object spatializers; custom codec/bank format; FMOD file compatibility (`.fspro`, `.bank` — proprietary); platform-specific hardware decoders; mobile/web native binaries for `Dirge.Native` in v1.

## 4. Architecture

```
Dirge.Model        (netstandard2.1 + net10; no Godot)  events, params, snapshots, mixer graph, clock, scheduler, evaluation, serialization, validation
Dirge.Runtime      (Godot .NET)                       Resources, AudioServer driver, player pool, 3D attributes, metering, LUFS, live-update host, GDScript bridge
Dirge.Tool         (Godot .NET + NecronomiDOM)        EditorPlugin dock, in-game overlay, HTML/CSS documents, typed bindings onto Model
Dirge.Native       (GDExtension, optional)            AudioEffectSend/Return, ConvolutionReverb; godot-cpp pinned, SYSTEM headers, -Werror on owned code
```

Evaluation loop per `Update(dt)`: inputs (global/local params, listener, 3D attrs, snapshot intensities) → model emits `Command[]` (`PlayerPlay/Seek/Stop/SetVolume/SetPitch`, `BusVolume/Mute/Solo`, `EffectParam`) → Runtime applies to `AudioServer`. Commands are the T1 oracle.

## 5. Data model

Canonical form is the `Dirge.Model` object graph serialized as JSON (`*.dirge.json`, System.Text.Json, deterministic key order) — text, diffable, loadable without the engine. Typed Godot `Resource` wrappers for Inspector editing arrive with the tool (M2+); they are views over the same model, not a second schema.

`AudioProject` → `AudioBank[]`, `MixerGraph` (`Bus[]`, `Vca[]`, `Return[]`), `Snapshot[]`, `GlobalParameter[]`, `AudioTable[]`.
`AudioEvent` → `Macros`, `Parameter[]` (local), `Track[]` → `Instrument[]` (typed), `AutomationLane[]`, `Modulator[]`, `LogicMarker[]`, `TempoMarker[]`.
All identity by GUID + stable path; renames don't break references.

## 6. Milestones

1. **M1 Model+Driver** — P0 model, serialization, validation, command emission; headless T1 harness; one event audible via driver script. No UI.
2. **M2 Mixer** — bus strip, meters, effect chain, snapshots in NecronomiDOM dock. First store-publishable cut (mixer-only).
3. **M3 Events** — event editor, instruments, parameters, automation, modulators, 3D preview.
4. **M4 Music** — tempo map, logic markers, quantized transitions, `AudioStreamInteractive`/`Synchronized` stitching, beat callbacks.
5. **M5 Live** — profiler, recordings, live update, in-game overlay, LUFS. v1.0.
6. **M6 Native** — send/return, convolution reverb. v1.1.

## 7. Acceptance (v1.0)

- Every P0 row has a T0/T1 test or a T2 render fixture; CI green headless on Linux/Windows/macOS.
- Demo project: 3D sandbox with ambience scatterer, adaptive music with beat-quantized transitions, ducking snapshot, programmer dialogue with audio table.
- Profiler recording of the demo replays bit-identical command stream (seeded).
- Addon ZIP installs into a fresh stock 4.7.2 .NET project alongside NecronomiDOM 1.1.4 with no warnings.
