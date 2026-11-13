# Getting started with Dirge

A ten-minute walkthrough: install the addon, author a bus, an event with a clip, a parameter with automation and a snapshot in the Dirge main screen, then play it from game code. It assumes stock Godot 4.7.2; no .NET installation is needed.

## 1. Install (2 minutes)

1. Copy `addons/dirge/` from the matching release ZIP into your project.
2. Install the matching bridge-enabled **native NecronomiDOM GDExtension** into `addons/necronomidom/`. Released 1.1.5 is insufficient. Use 1.1.6 or newer: `dependency-lock.json` pins its released runtime-only package for Linux x64/ARM64, Windows x64 and macOS ARM64/x64; see [package identity](../README.md#building-from-source). Keep the native binaries and notices; the HTML/CSS UI still requires them.
3. Open the project in stock Godot 4.7.2, then enable **Dirge** in **Project → Project Settings → Plugins**. A **Dirge** button appears in the editor's main-screen bar next to 2D/3D/Script, and two autoloads are registered: `DirgeServer` (runtime) and `DirgeOverlay` (debug-build overlay on **F9**).

No `.csproj` imports, NuGet packages, `dotnet build`, or external executable are required. NecronomiDOM's NativeAOT build toolchain is needed only when compiling that dependency from source, not when loading its precompiled GDExtension.

## 2. Create a project (1 minute)

Open the **Dirge** main screen. The menu bar is **File → New / Open… / Save / Save As…**, **Edit → Undo / Redo**, **Project → Validate / Build Banks**, **Live → Connect…**.

1. **New** creates an empty project with a `Master` bus.
2. **Save As…** and pick a path such as `res://audio/game.dirge.json`. The file is plain JSON with deterministic key order — commit it.
3. Open **Project → Project Settings → General**, search for `dirge/project_path` and set it to that file. `DirgeServer` loads it when the game starts.

The status bar at the bottom shows the file path, validation counts and the audition's instance/voice/update readouts.

## 3. Add a bus (1 minute)

In the **Mixer** tab (bottom):

1. **+ Bus** adds a bus routed to the selected bus (or Master). Double-click its name to rename it `Ambience`; drag strips to reorder.
2. Drag the fader to −3 dB; **M**/**S**/**B** mute, solo and bypass effects.
3. **Effects +** lists every `AudioEffect` the engine can instantiate. Add **Low Pass Filter**; click the effect chip to open its parameter popover and set the cutoff.
4. The routing button under the fader (`> Master`) re-parents the bus. **Max voices** caps real voices routed here (0 = unlimited).

Every fader move is pushed to the editor's `AudioServer` immediately, so you hear it while auditioning. Structural edits (add/remove bus, effect, re-route) rebuild the audition system, debounced.

## 4. Author an event with a clip (2 minutes)

1. In the **Browser → Events** tab press **+ Folder** (`Ambience`), then **+ Event** and name it `Wind`. Its path becomes `Ambience/Wind`, which is what game code uses.
2. In the event header set **Bus** to `Ambience`. **Macros** holds volume, pitch, priority, max instances, stealing mode, cooldown, persistent, 3D with min/max distance, lookahead and start quantization.
3. Switch the browser to **Assets**. It lists every audio file under `res://` with its length. Press **Import lengths** once so the project stores asset lengths — the model needs them to schedule one-shots without the engine. Drag `wind.wav` onto the timeline: a **Clip** instrument is created on a track at the drop position (snapped when **Snap** is on).
4. Select the block. The **Inspector** on the right shows asset, placement (start, length — 0 = natural), sync group, volume, pitch, random volume/pitch, start offset, fade in/out, **Loop**, **Async**, cut mode, polyphony, trigger delay, probability and trigger conditions. Turn **Loop** on.
5. Right-click the timeline for **Add marker → Loop** and resize the region to cover the clip, or leave the looping instrument to carry the event. Right-click also offers the other instrument kinds (Multi, Scatterer, Silence, Event, Programmer, Command, Snapshot), markers (Destination, Transition, Transition region, Magnet, Sustain point) and **Add tempo marker here**.
6. Press **Play** in the transport (or `Space`). **Stop All** clears every audition instance.

## 5. Add a parameter and automation (2 minutes)

1. In the event header open **Parameters** and press **+ Parameter**. Name it `Intensity`, range 0–1, kind Continuous. A new tab named `Intensity` appears next to **Timeline**: that is the parameter sheet, where instruments trigger over a parameter range instead of time.
2. Select the wind clip, and in **Automation & modulators** choose **Add lane → target…** → *Event volume*, parameter `Intensity`. A curve editor opens on the right: click to add points, drag them (snapped), right-click a segment to pick its shape (linear, smooth, stepped) or delete a point. Map 0 → −18 dB and 1 → 0 dB.
3. **Add modulator → LFO** on instrument volume with a small amount gives the wind a slow swell; **Random** picks a fixed offset per start; **AHDSR** envelopes the value from start to stop.
4. In the transport the event's parameters show up as sliders under **Event parameters**; drag `Intensity` while it plays to hear the curve. Parameters can also have asymmetric seek speeds and a hold flag (in the parameter row).

Global parameters (**Browser → Parameters**, **+ Parameter**) work the same way and are set from code with `SetGlobalParameter`; they can also drive mixer properties through project-level automation and quantize starts against a conductor event's tempo map.

## 6. Add a snapshot (1 minute)

1. In the **Mixer** tab press **+ Snapshot**, name it `Muffled`, and click it to open the snapshot editor.
2. Touch the `Ambience` fader and the low-pass cutoff in the effect popover — each touched control is **scoped** into the snapshot and highlighted; drag them to the values the snapshot should apply. **Capture** scopes every bus volume at once; the `x` badge unscopes.
3. Set **Mode** (Blending interpolates towards scoped values by intensity; Overriding replaces lower-priority snapshots), **Priority** and the intensity **AHDSR** (attack/hold/decay/sustain/release). Scrub **Intensity** to preview, then **Done**.

## 7. Validate and save (30 seconds)

**Project → Validate** (or the **Validation** tab) lists diagnostics with stable codes — missing master bus, routing cycles, duplicate names, missing assets, dangling references, unreachable instruments, unassigned parameters. The same check runs headless in CI:

```sh
scripts/validate.sh path/to/audio/game.dirge.json
```

`Ctrl+S` saves. **Build Banks** saves and reports what each bank preloads (banks are preload groups edited under **Browser → Banks**).

## 8. Play it in the game (1 minute)

GDScript:

```gdscript
func _ready() -> void:
    var wind := DirgeServer.StartEvent("Ambience/Wind")
    DirgeServer.SetParameter(wind, "Intensity", 0.3)

func _on_player_entered_cave() -> void:
    DirgeServer.StartSnapshot("Muffled")

func _on_player_left_cave() -> void:
    DirgeServer.StopSnapshot("Muffled")
```

Lower-level instance access is GDScript too:

```gdscript
var wind

func start_wind() -> void:
    var description = DirgeServer.System.get_event("Ambience/Wind")
    if description != null:
        wind = description.create_instance()
        wind.start()

func set_storm(intensity: float) -> void:
    if wind != null:
        wind.set_parameter("Intensity", intensity)

func _exit_tree() -> void:
    if wind != null:
        wind.stop("AllowFadeout")
        wind.release()
```

Migrating a typed C# consumer? See [migration](capabilities.md#migration-from-10-c-consumers).
Keep the public server's PascalCase names; use snake_case for lower-level runtime objects.
Existing `*.dirge.json` files keep their camelCase schema, string enums, 32-hex IDs and `$type` discriminators.

Run the game with `-- --dirge-live` and press **Live → Connect…** in the editor to see its instances in the Profiler and push mixer changes live; press **F9** in a debug build for the in-game Mixer/Profiler overlay.

## Where next

- [Capabilities and API reference](capabilities.md) — the full feature matrix and every public runtime member.
- [Specification](SPEC.md) — principles, architecture and acceptance criteria.
- `demo/sandbox/sandbox.dirge.json` — a complete authored project: scatterer, two-layer adaptive music with a beat-quantized transition, ducking snapshot, localized programmer dialogue.
