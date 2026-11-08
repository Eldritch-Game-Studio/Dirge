# Changelog

All notable changes to Dirge. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [Semantic Versioning](https://semver.org/).

## 1.0.2 — Unreleased

### Added
- Themes: the tool follows one `ToolTheme` palette selected by the new `dirge/theme` editor setting (also **View › Theme** in the menu bar, which cycles presets and reports the change in the status bar). **Editor** (default) derives the palette from the running Godot editor theme — base/dark/font/accent/error/warning/success colors — so a Light editor theme gets a light Dirge; **Dirge Dark** is the previous look; **Dirge Light** and **High Contrast** are built in; **Custom** derives from `dirge/theme_background`, `dirge/theme_foreground` and `dirge/theme_accent`. Derived palettes keep text at ≥ 4.5:1 and accent/semantic colors at ≥ 3:1 against the background; a Custom pair below the floor is corrected and warned about once. Documents receive the palette as inline custom properties on their root (`theme.css` keeps the Dirge Dark values as defaults and every pane stylesheet now uses variables only — new `--grid`, `--grid-strong`, `--playhead`, `--cursor`, `--on-dark`, `--on-light`, `--accent-hover`, `--accent-dim`, `--danger-dim`, `--success`, `--info`, `--meter-hold`, `--panel-master`, `--panel-snapshot`, mixer and `--type-*` tokens), and the timeline, curve, 3D panner, profiler graph and recording canvases draw from the same palette (instrument types are a themed palette of eight). Switching re-themes everything live without reopening the screen.

### Fixed
- The editor plugin re-added both autoloads every time the editor opened or a headless `--import` ran, and removed them whenever it left the tree. Each call can save `project.godot`, and a save normalizes the file (drops comments and default-valued settings, reorders keys, rewrites autoload paths as `uid://`); with a patched 4.7.2 engine every import rewrote a hand-maintained `project.godot`. The autoloads are now added only when missing and removed only when the plugin is disabled (`_DisablePlugin`). `scripts/smoke.sh` fails if an import changes the committed `project.godot`.
- Hi-DPI editors: the Event editor header, inspector and automation documents (and the Profiler document) rendered at scale 1 because their Godot container parents reset the host's `scale` and size on every layout pass; the timeline, curve, profiler and 3D-panner canvases clamped or hard-coded their label sizes. Every tool document and canvas now follows one `ToolScale` (editor display scale × main font size / 14, or the new `dirge/ui_scale` editor setting when non-zero; 0 = auto), hosts mount in plain `ToolDocuments.Slot` controls, authored minimum sizes rescale, and changing the setting (or the window DPI) re-scales live.

### Changed
- Event editor layout budget on small or hi-DPI screens: the header's Macros/Parameters/Tempo sections start collapsed (header costs one toolbar row; the toggles persist in editor setting `dirge/header_sections`), the bottom Mixer/Profiler/Validation tabs' minimum height is 180 px (was 280), the default top/bottom split gives the timeline at least 40 % of the center column, and the browser/center and top/bottom split positions persist across sessions (`dirge/split_browser`, `dirge/split_bottom`, in scale-1 pixels).

### Performance
- The tool is cheap when idle and no longer relayouts its documents every frame while auditioning. Measured in an offscreen editor with a KK-shaped project (250 events, 9 buses): idle `_Process` cost of the whole editor fell from ≈3.8 ms/frame (Dirge ≈2.4 ms, 23 document relayouts/s, ≈110 KB allocated per frame) to ≈0.6 ms/frame (Dirge within noise of 0, 0.1 relayouts/s, <1 KB/frame); auditioning fell from ≈100 ms to ≈10 ms/frame (median 1 ms) and the Profiler tab from ≈240 ms to ≈4 ms/frame. Changes: mixer meter bars, hold lines, profiler bus meters and transport slider fills are driven through inline `transform`s (NecronomiDOM composites them without relayout) instead of `height`/`width`/class writes; dB and timing readouts use fixed-width formats, 0.1 ms / 0.5 dB quantization and change-only writes so a changed digit is an in-place text swap; the transport readouts are single-text blocks; the Profiler pane stops processing while its bottom tab is hidden, the Mixer skips meter writes while hidden, the profiler feed only rebuilds its row lists at the document refresh rate (no per-frame enum/string allocation); the timeline playhead and the update graph redraw at most 30 times a second; validation re-runs 500 ms after the last edit instead of on every `Changed` (a fader drag fires one per frame); the Event editor ignores value-only mixer/snapshot edits (it only shows bus and snapshot names).

## 1.0.1 — 2026-11-07

### Fixed
- Timeline instruments whose region is shorter than one update tick (short one-shots under a long frame) were silently dropped; they now fire once and play their natural length.
- Export builds: editor-only API uses in the tool (`EditorInterface`, `EditorFileDialog`) are guarded with `TOOLS`; the in-game overlay keeps the runtime `FileDialog` path and scale 1.

## 1.0.0 — 2026-11-07

First release: adaptive-audio authoring and runtime for stock Godot 4.7.2 .NET, MIT licensed. Tool UI through NecronomiDOM ≥ 1.1.4; native effects are an optional GDExtension built for Linux x64, Windows x64 and macOS. Verified on this release: `dotnet build Dirge.slnx` (0 errors), `dotnet test tests/Dirge.Model.Tests` (257 passed), `scripts/smoke.sh` (demo and sandbox PASS) and the native send/return test scene `native/tests/sendreturn.tscn`. Documentation: `README.md`, `docs/getting-started.md`, `docs/capabilities.md` (feature matrix with per-row status and runtime API reference), `THIRD_PARTY_NOTICES.md`.

### Model and driver

- `Dirge.Model` (net8.0, no Godot reference): `AudioProject` graph — banks, mixer graph (buses, effects, VCAs), snapshots, global parameters, events with tracks, instruments, automation lanes, modulators, logic markers and tempo markers. Identity by `DirgeId` GUIDs plus stable paths.
- Instruments: Clip, Multi (random/sequential/shuffle, avoid-repeat, weights), Scatterer, Silence, Event (nested, depth limit 8), Programmer, Command, Snapshot. Instrument properties: volume, pitch, start offset, loop, fade in/out, cut mode, polyphony, trigger delay, playback percentage, conditions.
- Parameters: continuous, discrete, labeled; local and global; seek speed (asymmetric) and hold; built-ins computed from listener and 3D attributes (distance, normalized distance, direction, elevation, cone angle, orientation, speed).
- Evaluation: curves (linear, smooth, stepped), automation of any mixer or instrument property, modulators (random, AHDSR, LFO sine/square/triangle/saw/noise with tempo sync), snapshot blending by intensity with priority ordering and override mode, VCA summation.
- `AutopitchModulator` (FMOD autopitch): pitch ratio follows a local or global parameter piecewise-linearly in ratio space through (Minimum, `PitchAtMinimum`), (`ReferenceValue`, 1) and (Maximum, `PitchAtMaximum`); `Amount` scales the deviation from 1. Event-pitch and instrument-pitch targets only; `$type: "Autopitch"`; `ProjectValidator` checks the parameter resolves, Minimum ≤ Reference ≤ Maximum and both pitches are > 0 (`autopitch-invalid`).
- `AudioProject.MixerModulators`: LFO and Random modulators on mixer properties (bus volume/pitch, VCA volume, effect parameters) evaluated by `MixerState` every tick after automation — tremolo on a bus volume, auto-wobble on an effect parameter. Random offsets and Noise LFOs draw from the seeded system generator at construction; `MixerState.AddModulator`/`RemoveModulator` for live edits; validator rejects AHDSR/autopitch here (`mixer-modulator-invalid`).
- Runtime core: `DirgeSystem`, `EventDescription`, `EventInstance` with FMOD-equivalent lifecycle (start, stop immediate/fade-out, key-off, pause, release), macros (priority, max instances, stealing oldest/quietest/furthest/virtualize/none, cooldown, persistent, min/max distance, rolloff, doppler), global real-voice cap with virtualization, up to 8 weighted listeners, callbacks (started, stopped, marker, beat, sound played, virtualized, stolen).
- Spatial macros reach the engine: `EventMacros.Rolloff` (`Inverse`, `InverseSquare`, `Logarithmic`, `Disabled`; custom curve = `Disabled` + `Distance` automation) and `Doppler`/`DopplerScale` travel on `VoicePlay` and `AudioDriver` sets `attenuation_model`, `unit_size`, `max_distance` and `doppler_tracking` (physics step when doppler is on with a positive scale; Godot has no doppler scale, the magnitude is authored only) on every `AudioStreamPlayer3D`.
- Pre/post-fader effects: `Bus.FaderIndex` (default `int.MaxValue` = after the whole chain, the engine's bus volume). A smaller index makes `AudioDriver` insert an `AudioEffectAmplify` fader at that engine slot, hold the bus volume at 0 dB and route `BusSetVolume` to the amplify; authored effect indices stay model-side (`AudioDriver.EngineEffectIndex`, `FaderOf`), bypass mirrors the fader volume onto the bus while active. `ProjectValidator` rejects negative indices (`bus-fader-index-negative`). The mixer pane shows a draggable `FADER` row in each effect chain; dragging it or dragging an effect across it updates `FaderIndex`.
- Timeline: loop regions, transition markers and regions with beat/bar/marker quantization over the tempo map, sustain points, destination markers.
- Deterministic command stream (`VoicePlay/Seek/Stop/SetVolume/SetPitch/Set3D`, `BusSetVolume/Mute/Solo`, `EffectSetParameter`) emitted per `Update(dt)` with change detection; seeded RNG; zero steady-state allocation.
- `ProjectJson`: System.Text.Json text format (`*.dirge.json`) with deterministic key order and `$type` discriminators.
- `ProjectValidator`: structural diagnostics with stable codes (master bus, routing, cycles, duplicate names, missing assets, dangling references, unreachable instruments, unassigned parameters).
- Godot glue (`addons/dirge/runtime`): `DirgeServer` autoload (project from `ProjectPath` or the `dirge/project_path` setting; handle-based GDScript surface), `AudioDriver` over `AudioServer` with a pooled `AudioStreamPlayer`/`AudioStreamPlayer3D` set, `AssetCatalog`, `DirgePlugin` editor plugin registering the autoloads and the Dirge main screen.
- Demo project (`demo/`) with headless `--smoke` run; `scripts/smoke.sh`, `scripts/fetch-deps.sh`, `scripts/gen-demo-audio.py`.

### Mixer

- Editor main screen "Dirge" with menu bar (New/Open/Save/Save As/Validate/Build Banks/Connect), left browser, center event editor, bottom Mixer/Profiler tabs, top transport, validation panel and status bar; shared dark theme; undo/redo, save, play/stop and delete routed to the focused pane.
- Mixer pane: bus strips with faders, peak meters, mute/solo/bypass, routing picker, effect chain with per-effect parameter popovers, add/remove/reparent buses, VCAs, snapshot list and editor (scoped values, capture, intensity scrub, mode, priority).
- Ducking preset in the add-effect popup: pick a source bus and the chain gets an `AudioEffectCompressor` keyed from it (`sidechain` = source bus; threshold −24 dB, ratio 8:1, attack 20 ms, release 250 ms, gain 0), shown as "Ducking (from <bus>)".
- Live value push for volume and effect parameters without system rebuild; structural changes rebuild the audition system (debounced).
- Per-bus `LFO` button in the effect-section footer of every strip opens a popover listing the bus-volume LFOs (waveform picker, rate 0.05–20 Hz, depth 0–24 dB sliders with text fields, remove) with `+ LFO` adding a 4 Hz / 3 dB sine tremolo.
- In-game overlay (`DirgeOverlay` autoload, debug builds, F9 / `dirge_overlay` action) mounting the Mixer and Profiler panes over a running game.

### Events

- Event editor pane: header (path, bus, macros), timeline canvas with tracks, instrument blocks, markers, playhead, zoom/scroll, drag/resize, snap-to-grid, multi-select, copy/paste; parameter-sheet tabs; instrument inspector for all eight instrument types; automation lanes with curve editor; modulators; local parameters.
- Modulator forms offer Random, AHDSR, LFO and Autopitch; the autopitch item has a parameter picker (locals then `G:` globals), reference value and pitch-at-minimum/maximum fields (seeded with the default parameter at its default value, 0.5×…2×).
- Browser pane: events tree by path (folders, rename, new, delete, drag), assets tree from `res://` audio files with length readout, snapshots/parameters/banks lists, search, event tags (`AudioEvent.Tags`; filter chips under the search box, add/remove on the selected event in the Events footer).
- Transport pane: play/stop/stop-all, loop toggle, global and per-event parameter sliders, 3D preview panner driving the built-in parameters, voice/instance/ms readouts.

### Music

- Transition timelines: `TransitionMarker.TransitionLength` with transition-track instruments played between source and destination.
- Quantized event start (`EventMacros.StartQuantization`) against the conductor event (`AudioEvent.IsConductor`).
- Magnet regions snapping seeks to their position.
- Sample-locked multi-layer starts through `AudioStreamSynchronized` for instruments sharing `Instrument.SyncGroup`; transitions stay on the model clock (no `AudioStreamInteractive`).
- Parameter velocity (`EventInstance.GetParameterVelocity`).

### Live

- Profiler pane: live instance list, voice counts, update-time graph, per-bus level bars, recording start/stop/save/load/replay.
- `Dirge.Profiling`: `Recorder`, `RecordingJson`, `Replayer` with `Compare` returning the first divergent command — recordings double as deterministic test fixtures.
- Live update over localhost TCP (newline-delimited JSON, protocol 1, default port 7447): stats and instance feeds, full project replace (`DirgeServer.HotReload` keeps global parameter values, running snapshots and handles), live mixer value push, remote start/stop/parameter/snapshot; `--dirge-live[=port]` or `DirgeServer.LiveUpdatePort`; `--dirge-verbose` driver logging.
- `Dirge.Metering.LoudnessMeter`: ITU-R BS.1770-4 momentary/short-term/integrated LUFS with gating and 4× oversampled true peak; runtime meter on Master via `AudioEffectCapture`.
- Audio tables (key × locale → asset) resolved by the programmer instrument; `DirgeSystem.Locale`.
- Per-platform mixer overrides applied as the authored baseline; project-level mixer automation from global parameters.
- Banks: `LoadBank`/`UnloadBank`/`IsBankLoaded` preloading streams through threaded `ResourceLoader`.
- GDScript signal bridge on `DirgeServer` for handle-based instances: `EventStarted`, `EventStopped`, `TimelineMarker`, `TimelineBeat`, `SoundPlayed`, `EventVirtualized`, `EventUnvirtualized`, `EventStolen`, `ProgrammerSound` (answered synchronously with `SetProgrammerSound(handle, assetPath)`); `SetCallbackMask(handle, mask)` narrows a handle's traffic.
- 3D sandbox scene (`demo/sandbox`): ambience scatterer, two-layer adaptive music with beat-quantized Calm/Combat transition, ducking snapshot, two-locale programmer dialogue, UI click; headless smoke records, replays and compares a recording.
- `tools/dirge-validate` CLI: `dotnet run --project tools/dirge-validate -- file.dirge.json [--json]`; exit 1 on errors, 2 on parse failure.
- CI: unit tests, demo validation, stock-Godot headless smoke, native builds for Linux/Windows/macOS and the addon ZIP (`scripts/package.sh`, `scripts/ci-local.sh` for act).

### Native

- Optional GDExtension (`native/`, godot-cpp `10.0.0-stable` against engine API 4.7): `DirgeAudioEffectSend`/`DirgeAudioEffectReturn` pre/post-fader sends into return buses and `DirgeAudioEffectConvolutionReverb` (partitioned FFT). Registered through `addons/dirge/dirge_native.gdextension`; binaries under `addons/dirge/bin/<platform>/`. The mixer pane exposes sends and returns when the classes are present.

### Fixed before release

- `DirgeServer`: `--dirge-live[=port]`/`LiveUpdatePort` binds when the server loads a project, so the plugin's idle autoload no longer takes the port from a scene-local server (previously the scene server failed with `AlreadyInUse` and the editor connected to an empty server).
- Profiler "Game" source shares the shell's live connection instead of opening a second socket; the game keeps one editor connection, so the second socket used to drop the shell's and silently stop mixer pushes.
- `EditorAudition`: while Dirge owns the engine bus layout the editor's Audio Buses dock stops polling `AudioServer.is_edited()`, which otherwise saved the audition layout to `res://default_bus_layout.tres` and `audio/buses/default_bus_layout`; restoring the editor layout now also emits `bus_layout_changed` so the dock drops stale strips (no per-frame "Index p_bus out of bounds" errors after disabling the plugin).
- Model: one-shot instruments whose region is shorter than a tick are no longer swallowed — flash regions are swept in one segment and unplayed one-shot voices survive a same-tick exit.

### Known gaps (see `docs/capabilities.md`)

- `SoundStopped` has no GDScript signal (C# `EventInstance.Callback` only); no instance `SetProperty`.
- Transitions run on the model clock (`AudioStreamInteractive` not used); playlists are `MultiInstrument` modes; no plugin/generator instrument; no sidechain modulator (P2).
- Send/return requires the native extension (no degraded fallback); tool UI is English only. FMOD's doppler scale magnitude and its linear / linear-squared / inverse-tapered rolloff shapes have no engine equivalent (use `Disabled` + `Distance` automation).
