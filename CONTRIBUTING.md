# Contributing

Issues and pull requests are welcome.

## Setup

```sh
git clone --recurse-submodules https://github.com/Eldritch-Game-Studio/Dirge.git
cd Dirge
scripts/fetch-deps.sh        # exact bridge-enabled Neo native package into addons/necronomidom
godot --headless --path . --editor --import
```

Stock **Godot 4.7.2**, including the non-.NET editor, is the supported host; put its binary on `PATH` as `godot` or export `GODOT` (`Godot.app/Contents/MacOS/Godot` on macOS).

Dirge no longer uses managed source packages, `.csproj` imports, NuGet restore or a CLR model assembly. Use `scripts/package.sh` for the GDScript addon with its native audio GDExtension. Keep NecronomiDOM installed separately: the editor and overlay retain its native HTML/CSS implementation.
The required selector/mutation/scroll bridge is not released 1.1.5. `scripts/fetch-deps.sh` reads the exact path, hash and revision from `dependency-lock.json` (Neo source `99fa1910866590cfaa28f6c56489a5711bc8fddf`; package identity in the README). This local diagnostic package must already exist; it is not a published URL/version. Only Linux x64 was newly compiled/included. Neo Linux ARM64, Windows x64 and both macOS architectures remain required build prerequisites, not dropped support. Explicit overrides use `NECRONOMIDOM_PACKAGE_URL`, `NECRONOMIDOM_PACKAGE_SHA256` and matching `NECRONOMIDOM_REVISION`; never silently use latest.
Consuming the precompiled Neo NativeAOT GDExtension requires no .NET installation. Only rebuilding Neo from source requires its SDK/native compiler toolchain.

The Neo bridge passed stock non-.NET Godot 4.7.2 DOM/input/scroll/lifetime checks with clean teardown; this does not qualify integrated Dirge. Its NativeAOT build retains NU1903 for `Microsoft.DotNet.ILCompiler` 10.0.10 ([GHSA-p3jp-9v83-76w9](https://github.com/advisories/GHSA-p3jp-9v83-76w9)); no bridge runtime failure was observed, but the advisory remains outstanding.

## Before opening a pull request

```sh
godot --headless --path . --script tests/gdscript/run.gd # model/editor/driver suites
scripts/smoke.sh                       # import, GDScript suites, demo + sandbox + native headless smoke
native/build.sh                        # only when native/ changed
```

- `addons/dirge/model` holds GDScript data/evaluation/runtime logic; engine playback glue lives in `runtime/` and authoring glue in `tool/`. Models run under Godot without invoking `AudioServer` themselves.
- Behaviour changes need a model test or a smoke assertion; UI changes need a short description of how you verified them in the editor.
- Keep `docs/capabilities.md` in step with feature status and add a line to `CHANGELOG.md` under an `Unreleased` heading.
- CI script regressions run with `python3 -m unittest discover -s tests/scripts -v`.
- Commit the demo `*.dirge.json` files when authored data changes; they are plain JSON with deterministic key order.

## Releases

Bump `version` in `addons/dirge/plugin.cfg`, date the changelog entry, tag `v<version>` and push the tag. CI builds the native libraries for Linux, Windows and macOS, assembles `Dirge-v<version>.zip` and publishes the GitHub release.

The local Apple runner uses labels `[self-hosted, macOS, ARM64, eldritch-apple]`. It needs Xcode command-line
tools, Python/SCons, a universal stock Godot 4.7.2 editor and Rosetta for x64 execution.
`scripts/apple-run.py` holds the same `fcntl` exclusive
`$HOME/.local/state/eldritch-macos/producer.lock` as engine/game producers, forwarding termination to the
foreground process group. Invoke `python3 scripts/apple-run.py bash scripts/apple-native.sh` with `GODOT`
pointing to stock universal Godot and `RUNNER_TEMP` set to a job-specific temporary directory.
The release contract builds debug/release universal frameworks, verifies both slices with `lipo`, installs the
exact pinned Neo native dependency, and runs demo/sandbox/native smoke on both architectures. No .NET SDK/runtime is a Dirge runner prerequisite.

All CI/CD jobs, including PR admission and release publication, use local runners. The thin `ci.yml` caller
invokes `Eldritch-Game-Studio/Dirge/.github/workflows/ci-local.yml@main`. Both runner groups must restrict
workflow access to **`Eldritch-Game-Studio/Dirge/.github/workflows/ci-local.yml@refs/heads/main` only**,
not `ci.yml` or arbitrary PR workflows. Repository selection alone is insufficient protection.
The main-defined admission job performs no checkout, verifies the caller's collaborator permission, and resolves
the exact source SHA from GitHub's event/API. Internal PR heads qualify automatically for collaborator actors.
Unapproved forks/Dependabot receive a failing qualification check without executing source on either producer.
To approve reviewed external code, a collaborator dispatches **CI on main**, supplying `approved_pr` and
the exact 40-character reviewed `approved_head`; changed heads invalidate approval. Merely approving a fork's
workflow run does not grant access. Every build checks out the resolved immutable SHA without retaining
GitHub credentials; release publication is a separate no-build job.

The local `[self-hosted, Linux, X64, eldritch-linux]` runner needs Python 3, SCons, a native C++17 compiler,
`xvfb-run`/Xvfb/xauth, `x86_64-w64-mingw32-g++`/`x86_64-w64-mingw32-objdump`, and official
Proton 11 (`PROTON_HOME`, default `$HOME/.local/share/Steam/steamapps/common/Proton 11.0`).
Linux native output and Linux stock-Godot smoke remain Linux-specific. Windows x64 debug/release DLLs are
cross-built on Linux using pinned godot-cpp's supported MinGW path with static C++ runtimes; CI checks PE x64,
the `dirge_native_init` export and absence of unbundled MinGW runtime imports. Then
`timeout --signal=TERM --kill-after=30s 40m python3 scripts/windows-proton.py` downloads the matching
stock Windows Godot archive and verifies its SHA-256 against GitHub's HTTPS release metadata. It runs the
demo, sandbox and native send/return/convolution assertions against **each actual DLL** under official Proton.
No Windows .NET SDK, CLR runtime, MSBuild or managed compilation is required.
Each Godot invocation has a private Xvfb display and explicit engine log, so bootstrap cannot use the user's
display and assertions do not depend on Windows console forwarding. Any Godot `ERROR:`/`SCRIPT ERROR:`
fails qualification, including the first import attempt.
Prefix cleanup records `wineserver -k` and then bounded `-w` quiescence: an already-exited server's empty
exit-1 kill result is accepted only after a successful wait. Cleanup failure never replaces the primary error.
The stock editor uses debug feature selection; the release DLL is explicitly selected in a disposable
GDExtension mapping, never renamed or substituted. This is **Windows-on-Proton compatibility coverage**,
not native Windows, MSVC, or Windows release-export-template qualification; those remain unclaimed.
Every run uses a unique writable project/prefix beneath `RUNNER_TEMP` (or `/tmp`), bounded child processes,
and prefix-scoped cleanup; it neither starts Steam nor touches user game prefixes. Evidence records source,
DLL, stock Godot, native Neo dependency and Proton identities, actual command exit/timeout states and assertion results.
`DIRGE_PROTON_LOG_DIR` optionally selects the parent of the unique evidence directory. The private working
directory is retained for diagnosis; remove only that recorded directory once its processes have exited.
A full release remains blocked if any required platform or compatibility smoke fails; there is no hosted fallback.

The manual `partial=true` path and standalone `scripts/ci-local.sh` deliberately stay Linux-only, label the ZIP
`-partial`, and cannot publish a release. The standalone script uses local tools directly (no `act`, Docker or
approval token); set `GODOT` to a stock editor first. Normal packaging validates all six registered native
outputs, including both architectures in each Mac framework. This structural closure is not execution evidence.
Use `scripts/package.sh --require-qualification` to require a successful Proton receipt matching the
current source revision and both Windows DLL hashes. Strict release CI still requires Linux, Windows-on-Proton and macOS ARM64/x64 runtime closure.

These are required gates, not a statement that the 1.1.0 cutover has passed them. Prior .NET-era receipts,
screenshots and CI runs do not requalify the new scripts or Neo bridge. Record actual results at integration;
do not publish a full release while dependency pinning or any required platform qualification is missing.
