# Contributing

Issues and pull requests are welcome.

## Setup

```sh
git clone --recurse-submodules https://github.com/Eldritch-Game-Studio/Dirge.git
cd Dirge
scripts/fetch-deps.sh        # NecronomiDOM into addons/necronomidom (git-ignored)
dotnet build Dirge.slnx
```

Godot **4.7.2 .NET** (stock build) is the supported editor; put its binary on `PATH` as `godot` or export `GODOT` to the executable (`Godot_mono.app/Contents/MacOS/Godot` on macOS).

For a .NET host that already supplies native audio effects (such as Eldritch), commit source changes
and run `python3 scripts/package-managed.py`. It rebuilds the Release demo and writes a deterministic
`dist/dirge-managed-<commit>.zip` containing the exact committed addon source, its per-file hashes,
and upstream revision in `addon-package.json`. The host compiles the C# addon through `Dirge.targets`;
the package deliberately omits GDExtension registration and native binaries. Standalone users should
continue using the regular release package. Install the matching NecronomiDOM addon before building;
`unzip -oq <necronomidom-managed-package.zip> 'addons/necronomidom/*' -d .` installs a source package.
Host-generated `.import` files are excluded; installing this package preserves the host's asset-import settings.

## Before opening a pull request

```sh
dotnet test tests/Dirge.Model.Tests    # model unit tests
scripts/smoke.sh                       # import, build, demo + sandbox headless smoke
native/build.sh                        # only when native/ changed
```

- `Dirge.Model` (`addons/dirge/model`) must stay free of Godot references; engine glue lives in `runtime/` and `tool/`.
- Behaviour changes need a model test or a smoke assertion; UI changes need a short description of how you verified them in the editor.
- Keep `docs/capabilities.md` in step with feature status and add a line to `CHANGELOG.md` under an `Unreleased` heading.
- CI script regressions run with `python3 -m unittest discover -s tests/scripts -v`.
- Commit the demo `*.dirge.json` files when authored data changes; they are plain JSON with deterministic key order.

## Releases

Bump `version` in `addons/dirge/plugin.cfg`, date the changelog entry, tag `v<version>` and push the tag. CI builds the native libraries for Linux, Windows and macOS, assembles `Dirge-v<version>.zip` and publishes the GitHub release.

The local Apple runner uses labels `[self-hosted, macOS, ARM64, eldritch-apple]`. It needs Xcode command-line
tools (CLT 27.2), Python/SCons 4.10.1 in `$HOME/.local/share/eldritch-macos/tools/bin`, ARM64 .NET SDK 10.0.100
and runtime 8 in `$HOME/.dotnet`, matching Intel SDK/runtime in `$HOME/.dotnet-x64`, and Rosetta.
`scripts/apple-run.py` supplies the .NET roots/PATH and holds the same `fcntl` exclusive
`$HOME/.local/state/eldritch-macos/producer.lock` as engine/game producers, forwarding termination to the
foreground process group. Invoke `python3 scripts/apple-run.py bash scripts/apple-native.sh` with `GODOT`
pointing to stock universal Godot .NET and `RUNNER_TEMP` set to a job-specific temporary directory.
That command builds debug/release universal frameworks, verifies both slices with `lipo`, fetches the
unchanged version-pinned NecronomiDOM release dependency, and runs demo/sandbox/native smoke on both architectures.

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

PR events and exact-head manual approvals for the same PR share one concurrency group;
ordinary main dispatches share the main push group. Active qualification is not cancelled
by a new event. Concurrency serializes active runs; it does not deduplicate completed work.
A delayed PR event must still match the live PR head at admission or fail before checkout.
Do not dispatch a replacement merely because a provider reports zero jobs: that event can
still start later. Before source supersession, quiesce trusted admission routing, establish
that no old trust/build job was assigned, advance the PR head with the actual fix, then
restore routing. The old event's exact-head check excludes native work. If trust already
succeeded, reconcile its dependent jobs to terminal state instead; never replace evidence
or bypass the platform gate. Workflow changes take effect only after the trusted main
definition contains them. Neither PR nor manual qualification publishes a GitHub release;
publication remains tag-only, and Asset Store submission is a separate reconciled action.

The local `[self-hosted, Linux, X64, eldritch-linux]` runner needs Python 3, SCons, a native C++17 compiler,
.NET SDK 10.0.100/runtime 8, `xvfb-run`/Xvfb/xauth, `x86_64-w64-mingw32-g++`/`x86_64-w64-mingw32-objdump`, and official
Proton 11 (`PROTON_HOME`, default `$HOME/.local/share/Steam/steamapps/common/Proton 11.0`).
Linux native output and Linux stock-Godot smoke remain Linux-specific. Windows x64 debug/release DLLs are
cross-built on Linux using pinned godot-cpp's supported MinGW path with static C++ runtimes; CI checks PE x64,
the `dirge_native_init` export and absence of unbundled MinGW runtime imports. Then
`timeout --signal=TERM --kill-after=30s 40m python3 scripts/windows-proton.py` downloads the matching
stock Windows Godot .NET archive, verifies its SHA-256 against GitHub's HTTPS release metadata, and installs
the Windows .NET 8 SDK (including its runtime) verified against Microsoft's HTTPS SHA-512 release metadata. It runs the existing
demo, sandbox and native send/return/convolution assertions against **each actual DLL** under official Proton.
The Linux SDK builds portable managed IL with shared compiler/build-server reuse disabled. The genuine
Windows SDK supplies the stock editor's GodotTools/MSBuild assemblies; managed compilation stays on Linux.
Each Godot invocation has a private Xvfb display and explicit engine log, so bootstrap cannot use the user's
display and assertions do not depend on Windows console forwarding. Any Godot `ERROR:`/`SCRIPT ERROR:`
fails qualification, including the first import attempt. Native managed-build text/binlogs are retained.
Prefix cleanup records `wineserver -k` and then bounded `-w` quiescence: an already-exited server's empty
exit-1 kill result is accepted only after a successful wait. Cleanup failure never replaces the primary error.
The stock editor uses debug feature selection; the release DLL is explicitly selected in a disposable
GDExtension mapping, never renamed or substituted. This is **Windows-on-Proton compatibility coverage**,
not native Windows, MSVC, or Windows release-export-template qualification; those remain unclaimed.
Every run uses a unique writable project/prefix beneath `RUNNER_TEMP` (or `/tmp`), bounded child processes,
and prefix-scoped cleanup; it neither starts Steam nor touches user game prefixes. Evidence records source,
DLL, stock Godot, .NET and Proton identities, actual command exit/timeout states and assertion results.
`DIRGE_PROTON_LOG_DIR` optionally selects the parent of the unique evidence directory. The private working
directory is retained for diagnosis; remove only that recorded directory once its processes have exited.
A full release remains blocked if any required platform or compatibility smoke fails; there is no hosted fallback.

The manual `partial=true` path and standalone `scripts/ci-local.sh` deliberately stay Linux-only, label the ZIP
`-partial`, and cannot publish a release. The standalone script uses local tools directly (no `act`, Docker or
approval token); set `GODOT` to a stock .NET editor first. Normal packaging validates all six registered native
outputs, including both architectures in each Mac framework and a successful Proton receipt matching the
current source revision and both Windows DLL hashes. The receipt ships beside those qualified DLLs.
