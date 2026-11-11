# Dirge native (GDExtension)

C++17 audio effects for stock Godot 4.7 that the managed runtime cannot express with
built-in `AudioEffect`s: aux send/return routing and convolution reverb.

| Class | Properties | Role |
|---|---|---|
| `DirgeAudioEffectSend` | `target_bus: String`, `level_db: float` (−80..12), `pre_fader: bool` | Passes its input through and adds a scaled copy into the registry entry named `target_bus`. Godot applies the bus fader *after* the effect chain, so a post-fader send (`pre_fader = false`) multiplies by its own bus' current volume and mute (solo is not reflected); a pre-fader send ignores the fader. |
| `DirgeAudioEffectReturn` | — | Mixes every send addressed to the name of the bus it sits on into that bus (all senders summed), then clears the consumed frames. |
| `DirgeAudioEffectConvolutionReverb` | `impulse: AudioStream`, `wet_db`, `dry_db` (−80..12), `gain_db` (−80..24, output gain) | Uniformly partitioned frequency-domain convolution (own radix-2 FFT, 256-frame partitions, impulse capped at 4 s). A 2-channel impulse is true stereo (L∗IR_L, R∗IR_R); a mono impulse is dual mono. The impulse is decoded through its `AudioStreamPlayback` at the server mix rate at setup, so any `AudioStream` works; `AudioStreamWAV` mono/stereo is the intended input. Trailing silence is trimmed. |

`−80 dB` on any level means silence (gain 0).

## Routing rules

- **Bus order.** `AudioServer` processes buses from the highest index down to 0 and
  each bus hands its output to a lower-index bus. A return bus must therefore have a
  **lower index than every bus that sends to it**. If a sender is processed after
  the return, its frames are consumed one mix step late (not lost).
- **Bus activity.** The engine drops the output of a bus that has no playing source of
  its own, even when effects wrote into it. `DirgeAudioEffectReturn` therefore keeps its
  bus alive with a silent `AudioStreamPlayer` named `DirgeReturnKeepAlive_<bus>` parked
  under the scene root (created deferred on the main thread when the effect is
  instantiated and whenever its bus is renamed; stale players are pruned when a return
  disappears). Without a `SceneTree` main loop no keep-alive can be created.
- Send and return locate themselves in the layout by scanning `AudioServer` for their
  own effect instance (validated with one lookup per mix step, rescanned only when the
  layout changed). Multi-channel layouts (5.1/7.1) are routed per stereo pair.
- The registry (`SendBusRegistry`) is process-wide, keyed by bus name, mutex-guarded;
  entries are created on the main thread when `target_bus` is set (or on the audio
  thread once, when a return discovers its bus name) and live for the whole process.
  No allocation happens in `_process` after that.

## Build

```
git submodule update --init native/godot-cpp
pipx install scons            # or: pip install --user scons
native/build.sh               # Linux x86_64 template_debug + template_release
```

Output: `addons/dirge/bin/linux-x64/libdirge_native.linux.template_{debug,release}.x86_64.so`
(git-ignored), referenced by `addons/dirge/dirge_native.gdextension`
(`compatibility_minimum = 4.7`; entries for linux/windows x86_64 and macOS). The
SConstruct compiles owned sources with `-Wall -Wextra -Werror` (MSVC: `/W4 /WX`) and
includes godot-cpp as system headers (`-isystem` / `/external:I`). Other platforms:
`scons platform=windows target=<target> arch=x86_64` or
`scons platform=macos target=<target> arch=universal` from `native/`, for both
`template_debug` and `template_release`. A standalone release requires every configuration.

Mac CI runs on the local `eldritch-apple` ARM64 runner, with no hosted Mac dependency.
`python3 scripts/apple-run.py bash scripts/apple-native.sh` holds the shared producer lock,
builds both universal frameworks, verifies ARM64/x86_64 slices and runs stock-Godot .NET smoke
on both architectures (Intel via Rosetta). It requires `GODOT` and `RUNNER_TEMP`; see
[`CONTRIBUTING.md`](../CONTRIBUTING.md) for the .NET/toolchain paths. Linux native build/smoke
and Windows MinGW cross-build qualification use the local `eldritch-linux` runner.
`scripts/windows-native.sh` builds both Windows x64 configurations with static C++ runtimes and
checks PE architecture, the native entrypoint export and missing MinGW runtime dependencies.
The original Windows gate was compile/artifact-only; no Windows runtime execution is claimed.

`build.sh` drops an untracked `.gdignore` into `native/godot-cpp` (listed in the
submodule's `info/exclude`) so the Godot editor does not import godot-cpp's own test
project.

### godot-cpp pin

Submodule `native/godot-cpp` → https://github.com/godotengine/godot-cpp at tag
**`10.0.0-stable`**, commit `507ed9d840c01a3c5b2a39af8bb4000bfac30bf5`. godot-cpp is
versioned independently of the engine since 10.x and has no `godot-4.7.x-stable`
branch/tag; the build targets the engine API with `api_version=4.7`
(`gdextension/extension_api-4-7.json`, Godot 4.7-stable).

## Verify

```
godot --headless --audio-driver Dummy --path . res://native/tests/sendreturn.tscn
```

`native/tests/sendreturn.gd` builds buses `Master, Ret (Return), A (Send → "Ret",
volume −80 dB), Conv (ConvolutionReverb)` and checks: pre-fader send reaches `Ret` and
`Master` (> −40 dB) while `A` itself is silent (< −60 dB) and the keep-alive node exists;
post-fader send is silenced by `A`'s fader; a unit impulse and a unit impulse delayed by
100 000 frames (≈ 390 partitions) both reproduce the input level within 0.5 dB. Prints
`NATIVE_TEST PASS` and exits 0.
