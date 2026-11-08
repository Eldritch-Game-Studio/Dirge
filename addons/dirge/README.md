# Dirge

Adaptive audio authoring for Godot 4 .NET: events with timelines and parameter sheets, parameters with automation and modulators, snapshots, VCAs and a mixer graph played through `AudioServer`. The authoring tool is a Godot editor main screen rendered with NecronomiDOM; the same Mixer and Profiler panes mount over a running game.

Full documentation lives in the source repository: `README.md`, `docs/getting-started.md` (ten-minute walkthrough) and `docs/capabilities.md` (feature matrix and runtime API reference). The installable ZIP contains this addon folder, `LICENSE`, `README.md` and `CHANGELOG.md`.

## Installation

1. Copy this `addons/dirge/` folder into your project.
2. Install NecronomiDOM ≥ 1.1.5 into `addons/necronomidom/` (required by the editor tool and the in-game overlay; the runtime does not use it).
3. Add both imports to your project's `.csproj`, before `</Project>`, and build:
   ```xml
   <Import Project="addons/necronomidom/NecronomiDom.targets" />
   <Import Project="addons/dirge/Dirge.targets" />
   ```
4. Enable **Dirge** in **Project → Project Settings → Plugins**. This registers the `DirgeServer` and `DirgeOverlay` autoloads and adds the `dirge/project_path` project setting.
5. Open the **Dirge** main screen, **Save As…** a `*.dirge.json` project, and point `dirge/project_path` at it.

## Layout

- `model/` — `Dirge.Model`, engine-independent C# (built as its own assembly by `Dirge.targets`): project graph, evaluation, runtime, serialization, validation, profiling, metering.
- `runtime/` — Godot glue: `DirgeServer`, `AudioDriver`, `BankLoader`, `LoudnessCapture`, `LiveUpdateHost`/`LiveUpdateClient`, `DirgeOverlay`, `DirgePlugin`.
- `tool/` — the editor main screen and panes (HTML/CSS in `tool/ui/`).
- `dirge_native.gdextension` + `bin/<platform>/` — optional native effects `DirgeAudioEffectSend`, `DirgeAudioEffectReturn`, `DirgeAudioEffectConvolutionReverb`. Everything else works without them.

## Usage

GDScript: `DirgeServer.StartEvent("Ambience/Wind")` returns a handle for `SetParameter`, `StopEvent`, `IsEventPlaying`; `SetGlobalParameter`, `StartSnapshot`/`StopSnapshot`, `LoadBank`, `GetStats`.

C#: `GetNode<DirgeServer>("/root/DirgeServer").System` is the `DirgeSystem`; `GetEvent(path).CreateInstance()` gives an `EventInstance` with `Start`, `Stop`, `SetParameter`, `Set3DAttributes`, `Callback` and `Release`; `System.Mixer.StartSnapshot(name)` returns a `SnapshotInstance`.

Live update: run the game with `-- --dirge-live` and use **Live → Connect…** in the editor. In-game overlay: **F9** in debug builds.

## Compatibility

Requires **Godot 4.7.2 .NET**. Native effect binaries are provided for Windows x64, Linux x64 and macOS (universal); Android, iOS and Web are not supported by the native extension.

## License

MIT (see `LICENSE` in the repository root). Third-party components: godot-cpp (MIT), NecronomiDOM (MIT, installed separately), Noto Sans (SIL OFL 1.1, via NecronomiDOM). See `THIRD_PARTY_NOTICES.md`.
