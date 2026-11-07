# Changelog

All notable changes to Dirge. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [Semantic Versioning](https://semver.org/).

Items marked `(pending integration)` describe sibling milestone slices that were still landing when this entry was written; the integrator removes the marker once each item is verified on `main`.

## 1.0.0 — unreleased

First release: adaptive-audio authoring and runtime for stock Godot 4.7.2 .NET, MIT licensed. Tool UI through NecronomiDOM ≥ 1.1.4; native effects are an optional GDExtension.

### M1 — Model and driver

- `Dirge.Model` (net8.0, no Godot reference): `AudioProject` graph — banks, mixer graph (buses, effects, VCAs), snapshots, global parameters, events with tracks, instruments, automation lanes, modulators, logic markers and tempo markers. Identity by `DirgeId` GUIDs plus stable paths.
- Instruments: Clip, Multi (random/sequential/shuffle, avoid-repeat, weights), Scatterer, Silence, Event (nested, depth limit 8), Programmer, Command, Snapshot. Instrument properties: volume, pitch, start offset, loop, fade in/out, cut mode, polyphony, trigger delay, playback percentage, conditions.
- Parameters: continuous, discrete, labeled; local and global; seek speed (asymmetric) and hold; built-ins computed from listener and 3D attributes (distance, normalized distance, direction, elevation, cone angle, orientation, speed).
- Evaluation: curves (linear, smooth, stepped), automation of any mixer or instrument property, modulators (random, AHDSR, LFO sine/square/triangle/saw/noise with tempo sync), snapshot blending by intensity with priority ordering and override mode, VCA summation.
- Runtime core: `DirgeSystem`, `EventDescription`, `EventInstance` with FMOD-equivalent lifecycle (start, stop immediate/fade-out, key-off, pause, release), macros (priority, max instances, stealing oldest/quietest/furthest/virtualize/none, cooldown, persistent, min/max distance, doppler), global real-voice cap with virtualization, up to 8 weighted listeners, callbacks (started, stopped, marker, beat, sound played, virtualized, stolen).
- Timeline: loop regions, transition markers and regions with beat/bar/marker quantization over the tempo map, sustain points, destination markers.
- Deterministic command stream (`VoicePlay/Seek/Stop/SetVolume/SetPitch/Set3D`, `BusSetVolume/Mute/Solo`, `EffectSetParameter`) emitted per `Update(dt)` with change detection; seeded RNG; zero steady-state allocation.
- `ProjectJson`: System.Text.Json text format (`*.dirge.json`) with deterministic key order and `$type` discriminators.
- `ProjectValidator`: structural diagnostics with stable codes (master bus, routing, cycles, duplicate names, missing assets, dangling references, unreachable instruments, unassigned parameters).
- Godot glue (`addons/dirge/runtime`): `DirgeServer` autoload, `AudioDriver` over `AudioServer` with a pooled `AudioStreamPlayer`/`AudioStreamPlayer3D` set, `AssetCatalog`, `DirgePlugin` editor plugin.
- Demo project (`demo/`) with headless `--smoke` run; `scripts/smoke.sh`, `scripts/fetch-deps.sh`, `scripts/gen-demo-audio.py`.

### M2 — Mixer

- Editor main screen "Dirge" with menu bar (New/Open/Save/Save As/Validate/Build Banks/Connect), left browser, center event editor, bottom Mixer/Profiler tabs, top transport, validation panel and status bar; shared dark theme; undo/redo, save, play/stop and delete routed to the focused pane. (pending integration)
- Mixer pane: bus strips with faders, peak meters, mute/solo/bypass, routing picker, effect chain with per-effect parameter popovers, add/remove/reparent buses, VCAs, snapshot list and editor (scoped values, capture, intensity scrub, mode, priority). (pending integration)
- Live value push for volume and effect parameters without system rebuild; structural changes rebuild the audition system (debounced). (pending integration)
- In-game overlay (`DirgeOverlay`) mounting the same panes over a running game. (pending integration)

### M3 — Events

- Event editor pane: header (path, bus, macros), timeline canvas with tracks, instrument blocks, markers, playhead, zoom/scroll, drag/resize, snap-to-grid, multi-select, copy/paste; parameter-sheet tabs; instrument inspector for all eight instrument types; automation lanes with curve editor; modulators; local parameters. (pending integration)
- Browser pane: events tree by path (folders, rename, new, delete, drag), assets tree from `res://` audio files with length readout, snapshots/parameters/banks lists, search. (pending integration)
- Transport pane: play/stop/stop-all, loop toggle, global and per-event parameter sliders, 3D preview panner driving the built-in parameters, voice/instance/ms readouts. (pending integration)

### M4 — Music

- Transition timelines: `TransitionMarker.TransitionLength` with transition-track instruments played between source and destination. (pending integration)
- Quantized event start (`EventMacros.StartQuantization`) against the conductor event (`AudioEvent.IsConductor`). (pending integration)
- Magnet regions snapping seeks to their position. (pending integration)
- Sample-locked multi-layer starts through `AudioStreamSynchronized` for instruments sharing `Instrument.SyncGroup`; transitions stay on the model clock (no `AudioStreamInteractive`). (pending integration)
- Parameter velocity (`EventInstance.GetParameterVelocity`). (pending integration)

### M5 — Live (v1.0)

- Profiler pane: live instance list, voice counts, update-time graph, per-bus level bars, recording start/stop/save/load/replay. (pending integration)
- `Dirge.Profiling`: `Recorder`, `RecordingJson`, `Replayer` with `Compare` returning the first divergent command — recordings double as deterministic test fixtures. (pending integration)
- Live update over localhost TCP (newline-delimited JSON, protocol 1): stats and instance feeds, full project replace, live mixer value push, remote start/stop/parameter/snapshot; `--dirge-live[=port]`. (pending integration)
- `Dirge.Metering.LoudnessMeter`: ITU-R BS.1770-4 momentary/short-term/integrated LUFS with gating and 4× oversampled true peak; runtime meter on Master via `AudioEffectCapture`. (pending integration)
- Audio tables (key × locale → asset) resolved by the programmer instrument; `DirgeSystem.Locale`. (pending integration)
- Per-platform mixer overrides applied as the authored baseline; project-level mixer automation from global parameters. (pending integration)
- Banks: `LoadBank`/`UnloadBank`/`IsBankLoaded` preloading streams through threaded `ResourceLoader`. (pending integration)
- 3D sandbox scene (`demo/sandbox`): ambience scatterer, two-layer adaptive music with beat-quantized Calm/Combat transition, ducking snapshot, two-locale programmer dialogue, UI click; headless smoke records, replays and compares a recording. (pending integration)
- `tools/dirge-validate` CLI: `dotnet run --project tools/dirge-validate -- file.dirge.json [--json]`; exit 1 on errors, 2 on parse failure.
- CI: unit tests, demo validation, stock-Godot headless smoke, native builds for Linux/Windows/macOS and the addon ZIP (`scripts/package.sh`, `scripts/ci-local.sh` for act).

### M6 — Native (v1.1 preview)

- Optional GDExtension (`native/`, godot-cpp pinned to 4.7.2): `DirgeAudioEffectSend`/`DirgeAudioEffectReturn` pre/post-fader sends into return buses and `DirgeAudioEffectConvolutionReverb` (partitioned FFT). Registered through `addons/dirge/dirge_native.gdextension`; binaries under `addons/dirge/bin/<platform>/`. The mixer pane exposes sends and returns when the classes are present. (pending integration)
