#!/usr/bin/env python3
"""Exercise installed addon bytes with official, standard Godot; never a source overlay."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import os
import platform
import shutil
import subprocess
import time
import zipfile


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def install(archive, destination, prefix):
    with zipfile.ZipFile(archive) as package:
        names = package.namelist()
        if len(names) != len(set(names)):
            raise ValueError("duplicate ZIP entries")
        for info in package.infolist():
            path = PurePosixPath(info.filename)
            if path.is_absolute() or ".." in path.parts or not info.filename.startswith(prefix):
                raise ValueError(f"unexpected addon entry: {info.filename}")
            if path.suffix.lower() in {".cs", ".csproj", ".targets", ".sln", ".slnx", ".nupkg", ".snupkg", ".exe"}:
                raise ValueError(f"managed source/build payload or executable in runtime package: {path}")
            if (info.external_attr >> 16) & 0o170000 == 0o120000:
                raise ValueError(f"symlinks are not accepted in runtime packages: {path}")
        package.extractall(destination)


def copy_fixtures(source, target):
    allowed = {".gd", ".tscn", ".tres", ".json", ".wav", ".ogg", ".mp3", ".png", ".html", ".css", ".cfg"}
    for relative in ("tests/gdscript", "demo", "native/tests"):
        root = source / relative
        for path in root.rglob("*"):
            if path.is_file() and path.suffix in allowed:
                destination = target / path.relative_to(source)
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(path, destination)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", required=True, type=Path)
    parser.add_argument("--package", required=True, type=Path)
    parser.add_argument("--dependency", required=True, type=Path)
    parser.add_argument("--dependency-sha256", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--gui", action="store_true")
    args = parser.parse_args()
    args.output = args.output.resolve()
    if args.output.exists():
        raise ValueError("qualification output must be fresh; prior evidence is never overwritten")
    if digest(args.dependency) != args.dependency_sha256.lower():
        raise ValueError("NecronomiDOM runtime ZIP checksum mismatch")
    args.output.mkdir(parents=True)
    (args.output / ".gdignore").touch()
    stage = args.output / "installed"
    stage.mkdir()
    install(args.package, stage, "addons/dirge/")
    install(args.dependency, stage, "addons/necronomidom/")
    source = Path(__file__).resolve().parent.parent
    copy_fixtures(source, stage)
    (stage / "project.godot").write_text('''config_version=5
[application]
config/name="Dirge 1.1 installed qualification"
run/main_scene="res://demo/sandbox/sandbox.tscn"
config/features=PackedStringArray("4.7", "Forward Plus")
[display]
window/size/viewport_width=1600
window/size/viewport_height=1000
[dirge]
project_path=""
[editor_plugins]
enabled=PackedStringArray("res://addons/dirge/plugin.cfg")
''')
    receipt = {"schema": 1, "status": "RUNNING", "platform": platform.platform(),
               "coverage": "linux-x64-stock" if platform.system() == "Linux" else platform.system(),
               "package": {"path": str(args.package.resolve()), "sha256": digest(args.package)},
               "necronomidom": {"path": str(args.dependency.resolve()), "sha256": digest(args.dependency)},
               "godot": {"path": str(args.godot.resolve()), "sha256": digest(args.godot)}, "runs": []}
    receipt_path = args.output / "qualification.json"

    def save():
        receipt_path.write_text(json.dumps(receipt, indent=2) + "\n")

    def run(name, arguments, marker=None, graphical=False, timeout=240):
        environment = os.environ.copy()
        for key, folder in (("XDG_CONFIG_HOME", "config"), ("XDG_DATA_HOME", "data"), ("XDG_CACHE_HOME", "cache")):
            directory = args.output / "user" / folder
            directory.mkdir(parents=True, exist_ok=True)
            environment[key] = str(directory)
        command = [str(args.godot.resolve()), "--path", str(stage), "--audio-driver", "Dummy"]
        if graphical:
            command = ["gamescope", "--backend", "headless", "--disable-color-management",
                       "-W", "1600", "-H", "1000", "-w", "1600", "-h", "1000", "--"] + command
            command += ["--display-driver", "x11", "--rendering-method", "forward_plus", "--resolution", "1600x1000", "--max-fps", "60"]
        else:
            command += ["--headless"]
        command += arguments
        print("+", " ".join(command), flush=True)
        entry = {"name": name, "command": command, "marker": marker}
        receipt["runs"].append(entry)
        started = time.monotonic()
        with (args.output / (name + ".log")).open("w") as log:
            try:
                process = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, timeout=timeout, env=environment)
                entry["exit_code"] = process.returncode
                entry["timed_out"] = False
            except subprocess.TimeoutExpired:
                entry["exit_code"] = None
                entry["timed_out"] = True
        entry["seconds"] = time.monotonic() - started
        output = (args.output / (name + ".log")).read_text()
        print(output, end="", flush=True)
        entry["marker_present"] = marker is None or marker in output
        entry["errors"] = [line for line in output.splitlines() if "SCRIPT ERROR:" in line or line.startswith("ERROR:")]
        save()
        if entry["exit_code"] != 0 or not entry["marker_present"] or entry["errors"]:
            raise RuntimeError(f"installed qualification failed at {name}; evidence retained in {args.output}")

    try:
        run("import", ["--import"])
        run("behavior", ["--script", "res://tests/gdscript/run.gd", "--", "--result=" + str(args.output / "tests.json")], "DIRGE_TEST PASS")
        run("demo", ["res://demo/demo.tscn", "--", "--smoke"], "DIRGE_SMOKE PASS")
        run("sandbox", ["res://demo/sandbox/sandbox.tscn", "--", "--smoke"], "SANDBOX_SMOKE PASS")
        run("native-effects", ["res://native/tests/sendreturn.tscn"], "NATIVE_TEST PASS")
        if args.gui:
            proof_addon = stage / "addons/dirge_editor_proof"
            shutil.copytree(stage / "tests/gdscript/editor_proof", proof_addon, ignore=shutil.ignore_patterns("*.uid"))
            configuration = stage / "project.godot"
            text = configuration.read_text().replace('"res://addons/dirge/plugin.cfg")', '"res://addons/dirge/plugin.cfg", "res://addons/dirge_editor_proof/plugin.cfg")')
            configuration.write_text(text)
            run("editor-gui", ["--editor", "--", "--dirge-proof-out=" + str(args.output)], "DIRGE_EDITOR_PROOF PASS", graphical=True)
            run("overlay-gui", ["--script", "res://tests/gdscript/installed_runtime.gd", "--", "--dirge-proof-out=" + str(args.output)], "DIRGE_OVERLAY_PROOF PASS", graphical=True)
        receipt["status"] = "PASS"
        print("DIRGE_INSTALLED_PACKAGE PASS")
    except BaseException:
        receipt["status"] = "FAIL"
        raise
    finally:
        save()


if __name__ == "__main__":
    main()
