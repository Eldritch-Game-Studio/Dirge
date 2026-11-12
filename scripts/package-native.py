#!/usr/bin/env python3
"""Validate GDScript/native package closure; runtime evidence is a separate opt-in gate."""
import argparse
import configparser
from pathlib import Path, PurePosixPath
import struct
import hashlib
import json
import re
import zipfile

REQUIRED_SCRIPTS = (
    "runtime/dirge_plugin.gd", "runtime/dirge_server.gd",
    "model/Serialization/project_json.gd", "model/Validation/project_validator.gd",
)
REQUIRED_UI = (
    "browser.css", "browser.html", "event-automation.css", "event-automation.html",
    "event-header.css", "event-header.html", "event-inspector.css", "event-inspector.html",
    "event.css", "mixer.css", "mixer.html", "profiler.css", "profiler.html",
    "shell.css", "shell.html", "shell-status.html", "theme.css",
    "transport.css", "transport.html", "validation.css", "validation.html",
)
FORBIDDEN_SUFFIXES = {".cs", ".csproj", ".fsproj", ".sln", ".slnx", ".targets", ".props",
                      ".exe", ".nupkg", ".snupkg", ".binlog", ".pdb"}


def universal_architectures(data):
    if len(data) < 8 or data[:4] != b"\xca\xfe\xba\xbe":
        raise ValueError("expected a universal Mach-O library")
    count = struct.unpack_from(">I", data, 4)[0]
    if count != 2 or len(data) < 8 + count * 20:
        raise ValueError("expected exactly arm64 and x86_64 slices")
    architectures = set()
    for index in range(count):
        cpu, _, offset, size, _ = struct.unpack_from(">IIIII", data, 8 + index * 20)
        if offset < 8 + count * 20 or size < 32 or offset + size > len(data):
            raise ValueError("invalid Mach-O slice bounds")
        if data[offset:offset + 4] != b"\xcf\xfa\xed\xfe" or struct.unpack_from("<I", data, offset + 4)[0] != cpu:
            raise ValueError("Mach-O slice does not match its architecture table")
        architectures.add(cpu)
    if architectures != {0x01000007, 0x0100000C}:
        raise ValueError("missing arm64 or x86_64 Mach-O slice")
    return architectures


def validate_windows_qualification(root, revision=None):
    receipt = root / "windows-x64/proton-qualification.json"
    try:
        evidence = json.loads(receipt.read_text())
    except (OSError, ValueError) as error:
        raise ValueError(f"missing or invalid Windows Proton qualification: {receipt}") from error
    if (evidence.get("schema") != 2 or evidence.get("status") != "PASS"
            or evidence.get("coverage") != "windows-x64-proton-compatibility"
            or evidence.get("godot_distribution") != "standard"
            or evidence.get("godot_version") != "4.7.2"
            or not revision or evidence.get("source_revision") != revision):
        raise ValueError("Windows Proton qualification does not match this source revision")
    for target in ("template_debug", "template_release"):
        path = root / "windows-x64" / f"libdirge_native.windows.{target}.x86_64.dll"
        item = evidence.get("targets", {}).get(target, {})
        if (item.get("status") != "PASS"
                or item.get("sha256") != hashlib.sha256(path.read_bytes()).hexdigest()
                or item.get("assertions") != ["DIRGE_TEST PASS", "DIRGE_SMOKE PASS", "SANDBOX_SMOKE PASS", "NATIVE_TEST PASS"]
                or any(item.get(step, {}).get("exit_code") != 0
                       or item.get(step, {}).get("timed_out") is not False
                       for step in ("smoke", "native"))):
            raise ValueError(f"Windows DLL lacks matching successful Proton qualification: {path}")


def validate(root, revision=None):
    for target in ("template_debug", "template_release"):
        for directory, suffix in (("linux-x64", "linux"), ("windows-x64", "windows")):
            extension = "so" if suffix == "linux" else "dll"
            path = root / directory / f"libdirge_native.{suffix}.{target}.x86_64.{extension}"
            if not path.is_file() or not path.read_bytes():
                raise ValueError(f"missing native library: {path}")
        name = f"libdirge_native.macos.{target}"
        path = root / "macos" / f"{name}.framework" / name
        if not path.is_file():
            raise ValueError(f"missing native library: {path}")
        universal_architectures(path.read_bytes())
    if revision is not None:
        validate_windows_qualification(root, revision)


def validate_archive(archive, revision=None, partial=False):
    """Check the shipped bytes, not merely the producer's staging directory."""
    with zipfile.ZipFile(archive) as package:
        names = package.namelist()
        if len(names) != len(set(names)):
            raise ValueError("duplicate archive members")
        if any(not name.startswith("addons/dirge/") or ".." in PurePosixPath(name).parts
               or "\\" in name for name in names):
            raise ValueError("all package members must be inside addons/dirge/")
        for name in names:
            path = PurePosixPath(name)
            if path.suffix.lower() in FORBIDDEN_SUFFIXES or name.lower().endswith(".cs.uid"):
                raise ValueError(f"managed source, executable or build output is not an addon asset: {name}")
            if set(path.parts) & {".git", ".github", ".godot", ".gitignore", ".gitattributes",
                                  "Thumbs.db", ".DS_Store", "__MACOSX", "__pycache__", "obj"}:
                raise ValueError(f"unnecessary package member: {name}")
            if name.startswith("addons/dirge/model/bin/"):
                raise ValueError(f"unnecessary package member: {name}")
        for relative in (*REQUIRED_SCRIPTS, *("tool/ui/" + item for item in REQUIRED_UI)):
            name = "addons/dirge/" + relative
            if name not in names or not package.read(name):
                raise ValueError(f"missing GDScript/UI asset: {name}")
        plugin = configparser.ConfigParser(interpolation=None)
        plugin.read_string(package.read("addons/dirge/plugin.cfg").decode())
        script = plugin.get("plugin", "script").strip('"')
        if not script.endswith(".gd") or "addons/dirge/" + script not in names:
            raise ValueError("plugin.cfg must reference a packaged GDScript plugin")
        for name in names:
            if name.endswith((".gd", ".tscn", ".tres", ".html", ".css")):
                for resource in re.findall(r"""res://addons/dirge/[^"'\s\)<>]+""", package.read(name).decode("utf-8")):
                    member = resource.removeprefix("res://")
                    present = any(item.startswith(member) for item in names) if member.endswith("/") else member in names
                    if not present:
                        raise ValueError(f"missing referenced addon resource: {resource} in {name}")
        for document in ("LICENSE", "README.md", "CHANGELOG.md", "THIRD_PARTY_NOTICES.md"):
            name = f"addons/dirge/{document}"
            if name not in names or not package.read(name):
                raise ValueError(f"missing package document: {name}")
        manifest = "addons/dirge/dirge_native.gdextension"
        if manifest not in names:
            raise ValueError(f"missing GDExtension manifest: {manifest}")
        configuration = configparser.ConfigParser(interpolation=None)
        configuration.read_string(package.read(manifest).decode("utf-8"))
        if not configuration.has_section("libraries") or not configuration["libraries"]:
            raise ValueError("GDExtension manifest has no libraries")
        if partial:
            return
        for mapping, value in configuration["libraries"].items():
            resource = value.strip().strip('"')
            if not resource.startswith("res://addons/dirge/"):
                raise ValueError(f"invalid native library resource: {resource}")
            member = resource.removeprefix("res://")
            if member.endswith(".framework"):
                member += "/" + PurePosixPath(member).stem
            if member not in names or not package.read(member):
                raise ValueError(f"missing native library for {mapping}: {member}")
        validate(zipfile.Path(package, "addons/dirge/bin/"), revision)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", nargs="?", type=Path)
    parser.add_argument("--partial", action="store_true", help="validate an explicitly partial diagnostic ZIP")
    parser.add_argument("--require-qualification", metavar="REVISION", help="also require current-source Windows Proton runtime evidence")
    args = parser.parse_args()
    if args.partial and (args.archive is None or not args.archive.name.endswith("-partial.zip")):
        parser.error("--partial requires a -partial.zip archive")
    try:
        repository = Path(__file__).resolve().parent.parent
        revision = args.require_qualification
        if args.archive is not None:
            validate_archive(args.archive, revision, args.partial)
        else:
            validate(repository / "addons/dirge/bin", revision)
    except (OSError, KeyError, ValueError, configparser.Error, zipfile.BadZipFile) as error:
        raise SystemExit(f"package: {error}")
