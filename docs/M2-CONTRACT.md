# M2–M6 contract — tool, runtime extensions, native

Binding for all slices. Shared code that already compiles: `addons/dirge/tool/Core/*.cs` (`ProjectDocument`, `UndoStack`, `ChangeScope`, `Selection`, `DragPayload`/`ToolDrag`, `ToolContext`, `IToolPane`, `IAudition`, `ToolDocuments.Create` — implemented by the shell). M1 contract (`docs/M1-CONTRACT.md`) remains in force for the model/runtime API.

## UI architecture (decided)

- **Pane-level composition.** The Dirge main screen is a Godot `Control` tree (splits/tabs). Each pane is an `IToolPane`. Inside a pane, forms/lists/strips/menus are **NecronomiDOM documents** (`DocumentHost` from `ToolDocuments.Create("mixer.html")`); precision canvases (timeline, curve editor, 3D preview panner) are **Godot `Control` subclasses with `_Draw`**. Never overlay a Godot Control on top of a document element; split the pane instead (e.g. `HSplitContainer{ DocumentHost | TimelineCanvas }`).
- NecronomiDOM supported profile is authoritative: `addons/necronomidom/README.md`, `core/Css/Data/runtime-capabilities.json`. Admitted elements: `a, body, button, div, html, img, input, main, p, section, span, textarea`. No grid/table, no z-index, no `<select>`/range inputs, no float, no box-shadow. Flex rows/columns, absolute positioning, transforms, opacity, borders, backgrounds, overflow/scroll, `[hidden]`. Pointer events (`pointerdown/move/up`, `wheel`), `click`, `input`, `keydown` are dispatched; faders/knobs are `div`s driven by pointer events from C#. Dropdowns are custom `div` popups. Every document honors `[hidden] { display: none !important; }`.
- **Styling**: one shared stylesheet `addons/dirge/tool/ui/theme.css` (shell owns) with custom properties (`--bg`, `--panel`, `--fg`, `--muted`, `--accent`, `--meter-*`, `--border`, spacing/typography). Pane stylesheets import it via `<link rel="stylesheet" href="theme.css">` first. Dark, dense, FMOD-Studio-like.
- **Editing**: panes never mutate `AudioProject` directly — always `ProjectDocument.Mutate/MutateContinuous`, with the narrowest `ChangeScope` and the subject id. Panes subscribe to `Changed` and refresh incrementally when possible.
- **Audition**: `IAudition` is editor playback. After `ChangeScope.Mixer|Snapshots|Events|EventContent|GlobalParameters` structural changes the shell rebuilds the `DirgeSystem` (debounced ~150 ms) and raises `Rebuilt`; value-only mixer changes (volume, effect param) are pushed live without rebuild.
- **Text/scale**: respect `EditorInterface.Singleton.GetEditorScale()` via `ApplyTextScale`. Shell handles it in `ToolDocuments.Create`.
- **Keyboard**: Ctrl+Z/Ctrl+Shift+Z/Ctrl+S/Space(play-stop)/Delete handled by the shell and routed to the focused pane via `IToolPane` … (shell adds an optional `IToolPaneCommands { bool Handle(ToolCommand) }` interface in `Core/`; panes may implement).

## Panes and files

| Slice | Owns | Pane(s) |
|---|---|---|
| Shell | `tool/Shell/**`, `tool/ui/theme.css`, `tool/ui/shell*.html/css`, `tool/ui/validation.html/css`, `tool/Core/ToolDocuments.Impl.cs`, `tool/Core/ToolCommands.cs`, `runtime/DirgePlugin.cs` (extend), `runtime/DirgeOverlay.cs` | Main screen "Dirge" tab, menu bar (New/Open/Save/Save As/Validate/Build Banks/Connect), layout: left Browser · center Event editor · bottom Mixer/Profiler tabs · top Transport; Validation panel; status bar; in-game overlay node that mounts the same panes over a running game |
| Mixer | `tool/Mixer/**`, `tool/ui/mixer*.html/css` | Bus strips (fader, meter, mute/solo/bypass, routing picker, effect chain with per-effect parameter popover via `ClassDB.ClassGetPropertyList`), add/remove/reparent bus, VCAs, snapshot list + editor (scoped values, capture, intensity scrub, mode/priority), return/send effects when `DirgeAudioEffectSend` class exists |
| Events | `tool/Events/**`, `tool/ui/event*.html/css` | Event header (path, bus, macros), timeline canvas (tracks, instruments as blocks, loop/transition/sustain/destination/tempo markers, playhead, zoom/scroll, drag/resize/snap-to-grid, multi-select, copy/paste), parameter sheet tabs, instrument inspector (all 8 types incl. multi-entry list, conditions), automation lanes + curve editor canvas, modulators, local parameters; audition: Space plays selected event, playhead follows `TimelinePosition` |
| Browser+Transport | `tool/Browser/**`, `tool/Transport/**`, `tool/ui/browser*.html/css`, `tool/ui/transport*.html/css` | Events tree by path (folders, rename, new, delete, drag `EventDrag`), Assets tree from `res://` audio files (`AssetDrag`, length readout via `AudioStream.GetLength`), Snapshots/Parameters/Banks lists, search; Transport: play/stop/stop-all, loop toggle, global + selected-event parameter sliders, 3D preview panner canvas (listener at origin, draggable emitter → `IAudition.PreviewEmitter`), voice/instance/ms readouts |
| Profiler | `tool/Profiler/**`, `tool/ui/profiler*.html/css` | Live instance list (event, state, position, params), voice counts, `LastUpdateMs` graph, per-bus level bars, recording start/stop/save/load/replay (model `Recording`), live-update connect to game |
| ModelExt | `model/**` additions (see below), tests | — |
| RuntimeExt | `runtime/LiveUpdate*.cs`, `runtime/LoudnessMeter.cs`, `runtime/DirgeServer.cs` (extend), `runtime/AudioDriver.cs` (extend), `runtime/Banks.cs` | — |
| Native | `native/**`, `addons/dirge/bin/` (ignored), `addons/dirge/dirge_native.gdextension` | — |
| Sandbox | `demo/sandbox/**`, `scripts/gen-sandbox-audio.py`, `demo/DemoProject.cs` (extend) | — |
| CI+Docs | `.github/workflows/ci.yml`, `scripts/ci-local.sh`, `scripts/package.sh`, `tools/dirge-validate/**`, `README.md`, `docs/capabilities.md`, `docs/getting-started.md`, `CHANGELOG.md` | — |

## Model extensions (ModelExt slice)

- `TransitionMarker.TransitionLength` — transition timeline: play `TransitionTrack` instruments (new `List<Instrument> TransitionInstruments` on `TransitionMarker`/`TransitionRegion`) for `TransitionLength` seconds between source and destination.
- `EventMacros.ScheduleLookahead` + new `EventMacros.StartQuantization` (`Quantization`) — `Start()` on an event with quantization defers until the next grid point of the **conductor** (new: `DirgeSystem.Conductor` = currently playing event flagged `AudioEvent.IsConductor`, else immediate).
- `ParameterDefinition.Velocity` (units/second rate-of-change feed: `ParameterState.Velocity` readable; `BuiltInParameter.ParameterVelocity` not needed — expose `EventInstance.GetParameterVelocity(name)`).
- `MagnetRegion : LogicMarker { Length }` — when a seek lands inside, snap to `Position`.
- `AudioTable { Id, Name, Dictionary<string key, Dictionary<string locale, AssetRef>> }` on `AudioProject.AudioTables`; `DirgeSystem.Locale` string; `ProgrammerInstrument` resolves `Key` via tables before raising the callback.
- `PlatformOverride { string Platform; List<SnapshotValue> Values }` on `AudioProject.PlatformOverrides`; `DirgeSystem(..., platform: string?)` applies matching overrides as authored baseline.
- Project-level `List<AutomationLane> MixerAutomation` on `AudioProject` (global parameter → mixer property); `MixerState` consumes it at construction.
- `Recording` (`Dirge.Profiling`): `Recorder` attached to a `DirgeSystem` captures `Update(dt)` ticks, every public API call (start/stop/param/snapshot/listener) and the emitted command stream with tick index; `RecordingJson` serialize/deserialize; `Replayer` drives a fresh `DirgeSystem(project, seed)` from a recording and `Compare` returns the first divergent (tick, index, expected, actual) or null. Acceptance §7 uses this.
- `Loudness` (`Dirge.Metering`, pure C#): ITU-R BS.1770-4 K-weighting biquads (48 kHz and 44.1 kHz coefficients, mono/stereo), 400 ms momentary, 3 s short-term, integrated with absolute −70 LUFS and relative −10 LU gating, true-peak via 4× oversampled FIR. API: `LoudnessMeter(int sampleRate, int channels)`, `Push(ReadOnlySpan<float> interleaved)`, `MomentaryLufs`, `ShortTermLufs`, `IntegratedLufs`, `TruePeakDbtp`, `Reset()`. Tests with a −23 LUFS sine fixture generated in-test (1 kHz, −23 dBFS sine ≈ −23 LUFS ±0.1).
- Keep `ProjectJson` round-trip + validator rules updated for every new type (ModelExt owns those edits for its additions).

## Runtime extensions (RuntimeExt slice)

- **Live update**: `runtime/LiveUpdateHost.cs` (game side, enabled by `DirgeServer.LiveUpdatePort` export, default 0 = off; `--dirge-live[=port]` CLI arg enables) and `runtime/LiveUpdateClient.cs` (editor side). Transport: `StreamPeerTcp` on 127.0.0.1, newline-delimited UTF-8 JSON. Messages (`type` field): `hello{version, projectName}`, `stats{time, instances, realVoices, updateMs, buses:[{name, left, right}]}` at 10 Hz, `instances[{handle, event, state, position, virtual}]` at 5 Hz, `project{json}` editor→game full project replace (game rebuilds system, restarts nothing), `mixer-set{bus, volumeDb}|{bus, effect, name, value}` editor→game live value push, `start{path}`, `stop{handle, immediate}`, `set-param{handle?, name, value}`, `snapshot{name, start}`. Versioned: `protocol: 1`.
- `runtime/LoudnessMeter.cs`: `AudioEffectCapture` on Master feeding `Dirge.Metering.LoudnessMeter`; exposed via `DirgeServer.Loudness` and in `stats`.
- `runtime/Banks.cs`: `DirgeServer.LoadBank(name)` preloads streams of the bank's events via `ResourceLoader.LoadThreadedRequest`; `UnloadBank` releases the cache entries; `IsBankLoaded`.
- `AudioDriver`: `AudioStreamSynchronized` for instruments on the same track flagged `Instrument.SyncGroup` (new int on `Instrument`, ModelExt adds it; 0 = none) so multi-layer music starts sample-locked; `AudioStreamInteractive` is **not** used (model clock owns transitions) — document.
- `DirgeServer`: platform string from `OS.GetName()`, locale from `TranslationServer.GetLocale()`, `--dirge-live` arg parsing, `Loudness`, `Stats` struct for the profiler.

## Native (Native slice)

`native/` GDExtension (C++17, godot-cpp pinned to the `godot-4.7.2-stable` branch/tag, SCons): classes `DirgeAudioEffectSend` (properties `target_bus: String`, `level_db: float`, `pre_fader: bool`) and `DirgeAudioEffectReturn` (receives), `DirgeAudioEffectConvolutionReverb` (`impulse: AudioStream`, `wet_db`, `dry_db`, partitioned FFT convolution). Send/Return share a per-frame ring buffer keyed by target bus name registered in a process-wide registry; Return mixes in during its `process`. Register with `.gdextension` at `addons/dirge/dirge_native.gdextension`, binaries to `addons/dirge/bin/<platform>/` (git-ignored). Build script `native/build.sh` (Linux x64 locally), CI matrix for win-x64/macos. Owned code `-Werror -Wall -Wextra`; godot-cpp headers `-isystem`. Verification: a headless GDScript/C# test scene that routes a generated tone through Send→Return and asserts the return bus peak > −80 dB and the direct bus silent when `level_db` of the source bus is −80.

## Sandbox (Sandbox slice)

`demo/sandbox/sandbox.tscn`: 3D scene (player camera as listener, moving emitter nodes) exercising: ambience scatterer (birds), adaptive music (two layers + Beat-quantized transition between Calm/Combat via a parameter `Threat`), ducking snapshot on dialogue, programmer dialogue from an `AudioTable` with two locales, UI click. Audio generated by `scripts/gen-sandbox-audio.py` (short synthesized loops at 120 BPM so transitions are audible; keep each file < 300 KB). The project is authored in `demo/DemoProject.cs` (extend) and serialized to `demo/sandbox/sandbox.dirge.json`. Headless smoke `-- --smoke` extended: records a `Recording`, replays it, asserts `Compare == null`.

## CI+Docs (CI slice)

- `.github/workflows/ci.yml`: jobs `test` (dotnet test), `smoke` (download stock Godot 4.7.2 mono + NecronomiDOM release, run `scripts/smoke.sh`), `native-linux/windows/macos`, `package` (ZIP of `addons/dirge` + bins, excluding `necronomidom`), `validate-cli`.
- `tools/dirge-validate`: .NET console (`dotnet run --project tools/dirge-validate -- path.dirge.json`) printing diagnostics, exit 1 on errors.
- Docs: README (install incl. NecronomiDOM requirement, quick start GDScript + C#, tool tour), `docs/getting-started.md`, `docs/capabilities.md` (feature matrix status from SPEC with Done/Partial/Planned), `CHANGELOG.md` 1.0.0.
