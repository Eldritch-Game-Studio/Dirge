# Third-party notices

Dirge is MIT licensed (see `LICENSE`). It depends on or bundles the following third-party components.

## godot-cpp (MIT)

The native GDExtension in `native/` is built against godot-cpp (`native/godot-cpp`, git submodule, tag `10.0.0-stable`, engine API 4.7). Copyright (c) 2017-present Godot Engine contributors. License: `native/godot-cpp/LICENSE.md`. The compiled libraries in `addons/dirge/bin/<platform>/` statically link godot-cpp.

## NecronomiDOM (MIT) — runtime dependency

The editor tool and the in-game overlay render through NecronomiDOM ≥ 1.1.5, which is installed separately into `addons/necronomidom/` (release ZIP or `scripts/fetch-deps.sh`) and is not bundled in the Dirge addon ZIP. Copyright (c) 2026 Eldritch Game Studio. License: `addons/necronomidom/LICENSE`. NecronomiDOM itself bundles AngleSharp (MIT), W3C webref data (MIT), Unicode bidi data (Unicode License v3) and the font below; see its README.

## Noto Sans (SIL Open Font License 1.1) — via NecronomiDOM

The tool UI text is set in Noto Sans as shipped by NecronomiDOM (`addons/necronomidom/fonts/`). Copyright 2014-2021 Adobe, with Reserved Font Name 'Noto'; Copyright 2014-2021 Google LLC. License: `addons/necronomidom/fonts/OFL.txt`.

## Godot Engine (MIT)

Dirge targets stock Godot 4.7.2 .NET and its GDExtension API. Copyright (c) 2014-present Godot Engine contributors; Copyright (c) 2007-2014 Juan Linietsky, Ariel Manzur.
