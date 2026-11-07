# Contributing

Issues and pull requests are welcome.

## Setup

```sh
git clone --recurse-submodules https://github.com/Eldritch-Game-Studio/Dirge.git
cd Dirge
scripts/fetch-deps.sh        # NecronomiDOM into addons/necronomidom (git-ignored)
dotnet build Dirge.slnx
```

Godot **4.7.2 .NET** (stock build) is the supported editor; put its binary on `PATH` as `godot` or export `GODOT=/path/to/Godot_v4.7.2-stable_mono_linux.x86_64`.

## Before opening a pull request

```sh
dotnet test tests/Dirge.Model.Tests    # model unit tests
scripts/smoke.sh                       # import, build, demo + sandbox headless smoke
native/build.sh                        # only when native/ changed
```

- `Dirge.Model` (`addons/dirge/model`) must stay free of Godot references; engine glue lives in `runtime/` and `tool/`.
- Behaviour changes need a model test or a smoke assertion; UI changes need a short description of how you verified them in the editor.
- Keep `docs/capabilities.md` in step with feature status and add a line to `CHANGELOG.md` under an `Unreleased` heading.
- Commit the demo `*.dirge.json` files when authored data changes; they are plain JSON with deterministic key order.

## Releases

Bump `version` in `addons/dirge/plugin.cfg`, date the changelog entry, tag `v<version>` and push the tag. CI builds the native libraries for Linux, Windows and macOS, assembles `Dirge-v<version>.zip` and publishes the GitHub release.
