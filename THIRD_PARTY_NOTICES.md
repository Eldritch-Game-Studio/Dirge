# Third-party notices

Dirge is MIT licensed (see `LICENSE`). It depends on or bundles the following third-party components.

## godot-cpp (MIT)

The native GDExtension in `native/` is built against godot-cpp (`native/godot-cpp`, git submodule, tag `10.0.0-stable`, engine API 4.7). The compiled libraries in `addons/dirge/bin/<platform>/` statically link godot-cpp. Its full license follows so the distributed addon retains the required notice without needing the source checkout.

### MIT License

Copyright (c) 2017-present Godot Engine contributors.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## NecronomiDOM (MIT) — runtime dependency

The editor tool and the in-game overlay render through NecronomiDOM's native HTML/CSS GDExtension, installed separately into `addons/necronomidom/` (reviewed native package or `scripts/fetch-deps.sh`) and not bundled in the Dirge addon ZIP. The required GDScript selector/mutation/scroll bridge needs NecronomiDOM 1.1.6; `dependency-lock.json` pins its released runtime-only package by URL, SHA-256 and source `9be2bdd22f1904525014501939afc493ef270592`. Copyright (c) 2026 Eldritch Game Studio. License: `addons/necronomidom/LICENSE`. NecronomiDOM itself bundles AngleSharp (MIT), W3C webref data (MIT), Unicode bidi data (Unicode License v3) and the font below; preserve its full accompanying notices, including `vendor/dotnet/THIRD-PARTY-NOTICES.txt` in the runtime dependency.

NecronomiDOM is implemented using .NET NativeAOT. Its precompiled native library includes applicable .NET runtime/AOT components (Microsoft and .NET contributors, MIT and accompanying third-party notices). Those indirect attributions remain required even though Dirge's GDScript consumer no longer installs or restores .NET/NuGet. Retain the dependency package's full license and third-party notices; Dirge's cutover does not relicense or remove them. Building Neo from source requires its .NET SDK and native compiler toolchain; loading its distributed GDExtension does not require a .NET installation.

## Noto Sans (SIL Open Font License 1.1) — via NecronomiDOM

The tool UI text is set in Noto Sans as shipped by NecronomiDOM (`addons/necronomidom/fonts/`). Copyright 2014-2021 Adobe, with Reserved Font Name 'Noto'; Copyright 2014-2021 Google LLC. License: `addons/necronomidom/fonts/OFL.txt`.

## Godot Engine (MIT)

Dirge targets stock Godot 4.7.2 (including its non-.NET build) and its GDExtension API. Copyright (c) 2014-present Godot Engine contributors; Copyright (c) 2007-2014 Juan Linietsky, Ariel Manzur.
