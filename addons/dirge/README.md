# Dirge

Adaptive audio authoring for stock Godot 4.7.2, including non-.NET projects: events with timelines and parameter sheets, parameters with automation and modulators, snapshots, VCAs and a mixer graph played through `AudioServer`. The authoring tool is a Godot editor main screen rendered with NecronomiDOM's native HTML/CSS GDExtension; the same Mixer and Profiler panes mount over a running game.

Full documentation lives in the source repository: `README.md`, `docs/getting-started.md` (ten-minute walkthrough) and `docs/capabilities.md` (feature matrix and runtime API reference). The installable ZIP keeps the full repository `README.md`, `LICENSE`, `CHANGELOG.md` and `THIRD_PARTY_NOTICES.md` inside `addons/dirge/`, with no ZIP-root documents.

## Installation

1. Copy this `addons/dirge/` folder into your project.
2. Install the matching bridge-enabled NecronomiDOM native package into `addons/necronomidom/` (required by the editor tool and in-game overlay; the runtime does not use it). Use NecronomiDOM 1.1.6 or newer; released 1.1.5 lacks the required selector/mutation/scroll bridge. The bundled `dependency-lock.json` pins its runtime-only release package (Linux x64/ARM64, Windows x64, macOS ARM64/x64) by URL, SHA-256 and source revision. See the full repository README for the package identity and compiler advisory.
3. Open in stock Godot 4.7.2. No `.csproj` imports, .NET installation, NuGet restore or build executable is required. Neo's .NET NativeAOT source/compiler toolchain is a dependency-build requirement only.
4. Enable **Dirge** in **Project → Project Settings → Plugins**. This registers the `DirgeServer` and `DirgeOverlay` autoloads and adds the `dirge/project_path` project setting.
5. Open the **Dirge** main screen, **Save As…** a `*.dirge.json` project, and point `dirge/project_path` at it.

## Layout

- `model/` — GDScript `RefCounted` runtime objects and Dictionary/Array authored data: project graph, evaluation, runtime, serialization, validation, profiling, metering. Headless tests run inside Godot, not a CLR assembly.
- `runtime/` — Godot glue: `DirgeServer`, `AudioDriver`, `BankLoader`, `LoudnessCapture`, `LiveUpdateHost`/`LiveUpdateClient`, `DirgeOverlay`, `DirgePlugin`.
- `tool/` — the editor main screen and panes (HTML/CSS in `tool/ui/`).
- `dirge_native.gdextension` + `bin/<platform>/` — optional native effects `DirgeAudioEffectSend`, `DirgeAudioEffectReturn`, `DirgeAudioEffectConvolutionReverb`. Everything else works without them.

## Usage

GDScript: `DirgeServer.StartEvent("Ambience/Wind")` returns a handle for `SetParameter`, `StopEvent`, `IsEventPlaying`; `SetGlobalParameter`, `StartSnapshot`/`StopSnapshot`, `LoadBank`, `GetStats`.

Full instance API: `DirgeServer.System.get_event(path).create_instance()` returns a GDScript runtime object with `start`, `stop`, `set_parameter`, `set_3d_attributes`, the `callback` signal and `release`; `System.mixer.start_snapshot(name)` returns a snapshot instance. Unknown events return null. Public `DirgeServer` methods/signals retain PascalCase; model members use snake_case and authored Dictionary keys retain camelCase.

Existing `*.dirge.json` data retains its string enums, 32-hex IDs and `$type` discriminators. Typed C# consumer APIs and assembly imports are replaced, not shimmed. See the migration section in `docs/capabilities.md`.

The server uses a monotonic wall clock rather than the engine's smoothed/scaled frame delta. Starts and seeks are stamped at the mutation boundary: only subsequent elapsed time advances that instance on its next tick; other instances keep the whole interval. The first tick after load or pause/disable still advances zero seconds. Native play/seek commands use the resulting voice position, and profiler replay records the boundary offset without requiring a live clock. Direct `DirgeSystem.update(dt)` callers retain tick-boundary timing unless they supply the driver's elapsed-time clock.

Live update: run the game with `-- --dirge-live` and use **Live → Connect…** in the editor. In-game overlay: **F9** in debug builds.

Editor responsiveness: browser selection updates only the old/new rows; unchanged tag chips, transport parameters, event-toolbar controls and effect rows stay mounted. Mixer refreshes follow mixer/snapshot change scopes instead of rebuilding for unrelated edits. Retained controls resolve their current event when invoked, so selection changes and undo/redo do not leave stale edit targets. NecronomiDOM rematches selectors while reusing computed values only when their source generation, inputs and dependencies remain valid.

For UI profiling, launch the editor with `NECRONOMIDOM_UPDATE_TIMING=1`. Thresholded per-document reconciliation, layout, paint and allocation records are written to `user://necronomidom-document-updates-*.log`; these measure main-thread document work, not whole-frame/GPU latency.

## Compatibility

Requires **stock Godot 4.7.2**, not the .NET edition. Native audio package closure is six debug/release libraries for Windows x64, Linux x64 and macOS universal (ARM64/x64); Android, iOS and Web native effects are unsupported. NativeAOT NecronomiDOM remains a separate dependency. Historical screenshots and prior-version qualification do not requalify 1.1.0; no new platform/test/CI success is asserted here.

## License

MIT (see `LICENSE` in the repository root, or inside `addons/dirge/` in the installable ZIP). Third-party components: godot-cpp (MIT), NecronomiDOM (MIT, installed separately), Noto Sans (SIL OFL 1.1, via NecronomiDOM). See `THIRD_PARTY_NOTICES.md` alongside the license.
