# Dirge — adaptive audio authoring for Godot

Named Dirge. Standalone MIT addon for stock Godot 4.7.2, including non-.NET projects. UI remains HTML/CSS through NecronomiDOM's native GDExtension. The required selector/mutation/scroll bridge needs NecronomiDOM 1.1.6: `dependency-lock.json` pins its released runtime-only package for Linux x64/ARM64, Windows x64 and macOS ARM64/x64 by URL, SHA-256 and source revision. NativeAOT source/compiler requirements apply to building Neo, not consuming its precompiled package. Reference: FMOD Studio 2.03 feature set. Target: parity on everything a game team uses day to day, better on authoring ergonomics, testability and version control.

## 1. Principles

- **Separated model.** `addons/dirge/model` contains GDScript data/evaluation/runtime logic, tested headlessly inside Godot without a separate CLR assembly. Playback engine glue lives in `runtime/`; authoring glue lives in `tool/`.
- **One DSP path.** Playback is Godot's `AudioServer`. The tool auditions through the same path the game ships. No second engine, no browser audio.
- **Authored data is config, not state.** Dictionary/Array data is persisted as compatible `*.dirge.json`; runtime state lives in GDScript objects owned by the runtime service.
- **Native only when Godot can't.** GDExtension effects are a separate optional package (`Dirge.Native`), added per concrete missing effect. The addon is fully usable without it.
- **Verification tiers.** Every feature below carries the cheapest tier that proves it: T0 model unit test · T1 headless Godot (`--headless`, `AudioServer` with Dummy driver, command log) · T2 offline render comparison (`AudioEffectCapture` → WAV → tolerance) · T3 interactive/manual.

## 2. Priority tiers

- **P0** — parity blocker; v1.0 ships with all P0.
- **P1** — parity; v1.x.
- **P2** — parity requiring native, or "better" features; after v1.
- **—** — deliberate non-goal.

## 3. Feature matrix

The **Status** column retains the baseline feature scope: Done, Partial (with the gap), Planned. It does not assert a fresh 1.1.0 test or platform pass. PascalCase type names in this matrix label schema concepts, not importable C# types. Current GDScript API and migration details are in [`capabilities.md`](capabilities.md).

### 3.1 Events

| FMOD | Ours | P | Godot mechanism | Gap / note | Status |
|---|---|---|---|---|---|
| Event (timeline + parameter sheets) | `AudioEvent` Dictionary: tracks, instruments, parameter sheets, macros | P0 | model → `AudioStreamPlayer` pool | — | Done |
| Event instance lifecycle (create/start/stop immediate·fadeout/release) | `EventInstance` API identical semantics | P0 | pool + fade via `volume_db` tween in model clock | — | Done |
| Nested / referenced events | `EventInstrument` referencing another `AudioEvent` | P0 | recursive instance | depth limit 8, cycle check at validate | Done |
| Event macros: priority, max instances, stealing (oldest / quietest / furthest / virtualize / none), cooldown, persistent, min distance / max distance, rolloff, doppler, schedule delay / lookahead | all | P0 | model scheduler; virtualize = instance keeps clock, releases player | lookahead maps to `AudioStreamPlayer.play(from)` at predicted mix time; doppler scale has no engine equivalent (on/off only) | Done |
| Voice stealing at global polyphony cap | global + per-bus voice caps | P0 | pool size + `AudioServer` output latency | — | Done |
| Timeline markers: loop region, transition marker, transition region, sustain point, destination marker, tempo marker, magnet region | all except magnet | P0 (magnet P1) | model clock drives `seek`; sample-accurate via `AudioStreamSynchronized` / `AudioStreamInteractive` where clip-level | Godot stream seek is block-quantized (~10 ms at default latency); sample-accurate stitching requires `AudioStreamInteractive` clips → see 3.3 | Done |
| Transition quantization (beat/bar/marker, with transition timeline) | yes | P0 | tempo markers + model clock | — | Done |
| Trigger conditions: parameter range, probability, labeled value | yes | P0 | model | — | Done |
| Async instruments, cut-off on condition exit | yes | P0 | model | — | Done |
| Callbacks: started/stopped/timeline marker/beat/sound played/virtualized/stolen | GDScript instance callback Dictionaries and PascalCase server signals | P0 | — | — | Done (handle signals include `ProgrammerSound` + synchronous `SetProgrammerSound`) |
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
| Plugin instrument (synth) | `GeneratorInstrument` over `AudioStreamGenerator` | P2 | `AudioStreamGenerator` | script-side generator; native synth optional | Planned |
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
| Modulators: random, AHDSR, sidechain, autopitch, LFO (sine/square/tri/saw/noise, rate, phase, sync to tempo) | all | P0 (sidechain P2) | sidechain needs level data from another bus → capture RMS or native; autopitch = piecewise-linear pitch ratio through (min, pitch-at-min), (reference, 1), (max, pitch-at-max); LFO/Random also on mixer properties (`mixerModulators`) | Done (random/AHDSR/LFO/autopitch; sidechain P2 Planned) |
| Parameter conditions on instruments, transitions, logic markers | yes | P0 | | Done |

### 3.5 Mixer

| FMOD | Ours | P | Godot mechanism | Gap / note | Status |
|---|---|---|---|---|---|
| Buses hierarchy, master | yes | P0 | `AudioServer` buses | Godot bus = single parent send; hierarchy matches | Done |
| Group bus properties: volume, pitch, mute, solo, bypass | yes | P0 | `set_bus_volume_db/mute/solo/bypass_effects`; pitch via `AudioEffectPitchShift` | pitch on bus costs a pitch-shift effect; tool warns | Done |
| Return buses + sends (pre/post fader) | yes | P2 | **no aux sends in Godot** | native `DirgeAudioEffectSend` + `DirgeAudioEffectReturn`; no duplicate-route fallback | Done (native only; no degraded fallback) |
| VCAs | yes | P0 | model-side gain summation onto bus volume | — | Done |
| Effects: 3-EQ, multiband EQ, compressor, limiter, gain, delay, reverb, chorus, flanger, distortion, pitch shifter, tremolo, transceiver, convolution reverb, loudness meter, spectrum | EQ6/10/21, Compressor, Limiter/HardLimiter, Amplify, Delay, Reverb, Chorus, Phaser (flanger-equivalent), Distortion, PitchShift, StereoEnhance, Panner, Filter family, Capture, SpectrumAnalyzer | P0 | Godot built-ins | tremolo = mixer LFO on bus volume (strip `LFO` button) or effect parameter; transceiver = native send/return, convolution = native; LUFS uses `AudioEffectCapture` | Done (tremolo via mixer LFO; transceiver/convolution need native audio) |
| Pre/post fader effect position | yes | P0 | effect order + Amplify as "fader" when pre-fader effects needed | Godot applies bus volume after effects; `Bus.FaderIndex` < chain length inserts an `AudioEffectAmplify` fader at the split point, bus volume held at 0 dB, `BusSetVolume` drives the amplify; driver maps authored → engine indices | Done |
| Snapshots: blending by intensity, priority ordering, overriding vs blending, scoped properties, AHDSR on intensity | yes | P0 | model computes final bus/effect values each tick | **Better:** snapshot diff view in tool | Done |
| Sidechain compressor | yes | P0 | `AudioEffectCompressor.sidechain` (bus name) | native Godot feature | Done |
| Ducking presets | yes | P0 | sidechain compressor template | add-effect popup → source bus picker → compressor keyed from it | Done |
| Spatializer / object spatializer / HRTF | Godot 3D attenuation + panning strength | P0 (HRTF —) | `AudioStreamPlayer3D`, `AudioListener3D` | no HRTF in Godot; non-goal | Done |
| Rolloff: linear, linear squared, inverse, inverse tapered, custom curve | Godot: inverse, inverse square, logarithmic, disabled; custom via automation on distance | P0 | `EventMacros.Rolloff` → `attenuation_model` | custom curve via built-in `distance` parameter → volume automation with `Rolloff.Disabled` | Done (engine models; custom via `Distance` automation) |
| Doppler | yes | P0 | `doppler_tracking` (physics step) | engine has no doppler scale: `DopplerScale` > 0 enables, 0 disables | Done (on/off; scale authored only) |
| Occlusion/obstruction | parameter-driven (game supplies) + lowpass automation template | P1 | | FMOD also delegates to game geometry | Partial (parameter-driven; no template) |
| Per-platform mixer overrides | per-`OS.get_name()` override sets | P1 | | | Done |
| Loudness metering LUFS-M/S/I, true peak | yes | P1 | GDScript ITU-R BS.1770 over `AudioEffectCapture` on master | **Better than Godot**; parity with FMOD | Done |
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
| Scripting (FMOD: JavaScript) | GDScript API and Dictionary models | P0 | public server PascalCase; lower-level runtime snake_case | Done |
| Undo/redo, multi-select, copy/paste across events | yes | P0 | | Done |
| Localization audio tables | yes | P1 | key × locale → clip; programmer instrument resolver | Done |
| Banks: build, per-platform, streaming assets, bank loading API | **—** | — | Godot PCK + `ResourceLoader`; "bank" = a folder/`AudioBank` resource listing events for preload. Provide `LoadBank/UnloadBank` that preloads streams. | Done (`LoadBank`/`UnloadBank` preload) |
| Version control integration | **—** (text resources + git) | — | **Better:** no binary project file | Done |
| Localization of tool UI | P2 | | | Planned |

### 3.7 Runtime API (parity with FMOD Studio API)

Public `DirgeServer` methods/signals retain PascalCase; lower-level GDScript models use snake_case. See the complete current member tables and migration in [`capabilities.md`](capabilities.md#runtime-api-reference).

- Server: load/unload JSON projects, handle-based event control, parameters, snapshots, bank preloads, stats, hot reload and live update.
- System: Dictionary project, parameter bank, mixer, event descriptions, weighted listeners (up to 8), conductor, manual `update(dt)`, command Array and feedback.
- Event description: identity/path, spatial/one-shot traits, length, parameter definitions, instance count and `create_instance()`.
- Event instance: start, stop (immediate/fadeout), release, key-off, pause, local/global parameter lookup, label/velocity access, 3D attributes, volume/pitch, timeline seek, transition state and callbacks.
- Mixer: effective bus/VCA values, runtime volume/mute/solo overrides, automation and snapshot lifecycle/intensity.
- Command/feedback/callback payloads are Dictionaries; `type` preserves the original record name and remaining fields use camelCase.
- Thread model: invoke model and server APIs on Godot's main thread; no C# thread-safe API guarantee is carried over. The driver owns `AudioServer` interaction.
- Arbitrary instance property/user-data overrides remain Planned; this API overview does not silently implement them.

### 3.8 Performance targets

These are design targets, not measured GDScript-cutover results; in particular, Dictionary-based execution does not inherit the former CLR zero-allocation claim.

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
addons/dirge/model    (GDScript)                       Dictionary project graph, RefCounted runtime/evaluation, scheduler, serialization, validation
addons/dirge/runtime  (GDScript + AudioServer)         player pool, banks, 3D attributes, metering, live update, public DirgeServer
addons/dirge/tool     (GDScript + native Neo bridge)   editor main screen, overlay, retained HTML/CSS documents and canvases
Dirge native audio   (C++ GDExtension, optional)      send/return, convolution; six desktop debug/release libraries
```

Evaluation loop per `update(dt)`: inputs (global/local params, listener, 3D attrs, snapshot intensities) → model emits command Dictionaries (`VoicePlay/Seek/Stop/SetVolume/SetPitch`, `BusSetVolume/Mute/Solo`, `EffectSetParameter`) → runtime applies to `AudioServer`. Commands are the T1 oracle.

## 5. Data model

Canonical form is Dictionary/Array authored data serialized as JSON (`*.dirge.json`, deterministic key order) by `model/Serialization/project_json.gd`. Existing camelCase keys, string enum values, 32-hex IDs and `$type` discriminators remain unchanged. Runtime objects are separate state; there is no replacement `.tres` schema or required data conversion.

`AudioProject` → `AudioBank[]`, `MixerGraph` (`Bus[]`, `Vca[]`, `Return[]`), `Snapshot[]`, `GlobalParameter[]`, `AudioTable[]`.
`AudioEvent` → `Macros`, `Parameter[]` (local), `Track[]` → `Instrument[]` (typed), `AutomationLane[]`, `Modulator[]`, `LogicMarker[]`, `TempoMarker[]`.
All identity by GUID + stable path; renames don't break references.

## 6. Original milestone plan (historical, not a release-status claim)

1. **M1 Model+Driver** — P0 model, serialization, validation, command emission; headless T1 harness; one event audible via driver script. No UI.
2. **M2 Mixer** — bus strip, meters, effect chain, snapshots in NecronomiDOM dock. First store-publishable cut (mixer-only).
3. **M3 Events** — event editor, instruments, parameters, automation, modulators, 3D preview.
4. **M4 Music** — tempo map, logic markers, quantized transitions, `AudioStreamInteractive`/`Synchronized` stitching, beat callbacks.
5. **M5 Live** — profiler, recordings, live update, in-game overlay, LUFS. v1.0.
6. **M6 Native** — send/return, convolution reverb. v1.1.

## 7. Acceptance for the 1.1.0 cutover

- Preserve every implemented feature above and every explicit Partial/Planned limitation; migrate typed consumers without compatibility shims.
- Run `godot --headless --path . --script tests/gdscript/run.gd`, `scripts/validate.sh` and `scripts/smoke.sh` under stock non-.NET Godot 4.7.2.
- Exercise the 3D sandbox: ambience scatterer, adaptive beat-quantized music, ducking snapshot, programmer dialogue/audio table and overlay.
- Record/replay the demo and compare deterministic commands; historical CLR recordings require comparison rather than an assumed bit-identical RNG sequence.
- Install the addon into a fresh non-.NET project with the exact bridge-enabled native Neo dependency, no C# imports, .NET/NuGet install or executable payload requirement.
- Require six native audio libraries (Linux x64, Windows x64, universal macOS; debug/release), both Mac runtime architectures, and matching Windows-on-Proton receipts for strict release qualification. Do not claim native Windows/MSVC qualification.
- Preserve Neo's native HTML/CSS UI and dependency licenses; obtain fresh UI evidence rather than reusing 1.0 screenshots as proof.

These are acceptance criteria, not integrated Dirge results. Neo's exact released identity is pinned and its bridge passed stock non-.NET Godot 4.7.2 DOM/input/scroll/lifetime checks with clean teardown on every desktop target. Integrated Dirge platform/UI/test qualification uses each exact installed-package receipt; per-release results are in `CHANGELOG.md`. The Neo NativeAOT compiler NU1903 advisory remains outstanding (see [build notes](../README.md#building-from-source)). Release publication must not infer success from either bridge-only checks or old .NET-era receipts.
