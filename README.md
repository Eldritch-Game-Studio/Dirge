# Dirge

Adaptive audio authoring for stock Godot 4 .NET, with FMOD Studio feature parity as the target: events with timelines and
parameter sheets, instruments, local/global parameters with automation and modulators, snapshots, VCAs and a mixer graph
mapped onto `AudioServer`. MIT licensed. See `docs/SPEC.md` for the feature matrix and `docs/M1-CONTRACT.md` for the runtime API.

The model (`addons/dirge/model`, assembly `Dirge.Model`) is plain C# with no Godot reference and is unit-tested headless.
The Godot side (`addons/dirge/runtime`) hosts it: `DirgeServer` drives the model from `_Process`, `AudioDriver` applies the
emitted command stream to `AudioServer` and a pooled set of `AudioStreamPlayer`/`AudioStreamPlayer3D` nodes.

## Requirements

- Godot **4.7.2 .NET** (stock build) and a .NET SDK able to target `net8.0` (SDK 8 or newer; `global.json` pins 10.x with roll-forward).
- NecronomiDOM ≥ 1.1.4 is required by the forthcoming authoring UI (M2+). It is **not** needed for the M1 runtime.

## Install

1. Copy `addons/dirge` into your project's `addons/` folder.
2. Import the build targets once from your project's `.csproj` so `Dirge.Model` builds as its own assembly and `runtime/`
   compiles into your game assembly:
   ```xml
   <Import Project="addons/dirge/Dirge.targets" />
   ```
3. Enable the plugin in **Project → Project Settings → Plugins**. Enabling registers a `DirgeServer` autoload; disabling removes it.
   Alternatively add a `DirgeServer` node to a scene yourself (the demo does this) and set its `ProjectPath`.
4. Build the solution (`dotnet build`) and run.

`DirgeServer` loads the project JSON at `ProjectPath` in `_Ready`, rebuilds the `AudioServer` bus layout from the project's
mixer graph (Master stays at index 0) and then exposes:

- C#: `server.System` (`DirgeSystem`), `server.Driver`, `server.Load(AudioProject)`, `server.LoadFile(path)`, `server.GetInstance(handle)`.
- GDScript: `start_event(path) -> int`, `stop_event(handle, immediate)`, `set_parameter(handle, name, value)`,
  `set_global_parameter(name, value)`, `get_global_parameter(name)`, `start_snapshot(name)`, `is_event_playing(handle)`.

## Demo and smoke test

`demo/demo.tscn` plays a looping wind bed on an `Ambience` bus with a low-pass filter, a one-shot click, and a `Muffled`
snapshot (any key clicks, Enter toggles the snapshot). The project data lives in code (`demo/DemoProject.cs`); the checked-in
`demo/demo.dirge.json` is generated from it by the serializer the first time the scene runs without the file, so delete the
JSON and run the demo to regenerate it after editing `DemoProject.cs`. The demo WAVs come from `scripts/gen-demo-audio.py`.

The M1 headless harness (T1) runs the demo with the `Dummy` audio driver, scripts starts and a snapshot over 90 frames and
asserts on the applied commands and `AudioServer` state:

```sh
scripts/smoke.sh            # GODOT=/path/to/Godot_v4.7.2-stable_mono_linux.x86_64 to override the binary
```

It prints one `DIRGE_SMOKE <CommandKind> xN` line per command kind applied, the bus indices/volume it inspected, then
`DIRGE_SMOKE PASS` (exit 0) or `DIRGE_SMOKE FAIL: <reason>` (exit 1). Model unit tests: `dotnet test tests/Dirge.Model.Tests`.
