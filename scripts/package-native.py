#!/usr/bin/env python3
"""Reject incomplete standalone packages, including thin macOS libraries."""
from pathlib import Path
import struct
import hashlib
import json
import subprocess
import sys


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
    if (evidence.get("schema") != 1 or evidence.get("status") != "PASS"
            or evidence.get("coverage") != "windows-x64-proton-compatibility"
            or not evidence.get("source_revision")
            or (revision is not None and evidence["source_revision"] != revision)):
        raise ValueError("Windows Proton qualification does not match this source revision")
    for target in ("template_debug", "template_release"):
        path = root / "windows-x64" / f"libdirge_native.windows.{target}.x86_64.dll"
        item = evidence.get("targets", {}).get(target, {})
        if (item.get("status") != "PASS"
                or item.get("sha256") != hashlib.sha256(path.read_bytes()).hexdigest()
                or item.get("assertions") != ["DIRGE_SMOKE PASS", "SANDBOX_SMOKE PASS", "NATIVE_TEST PASS"]
                or any(item.get(step, {}).get("exit_code") != 0
                       or item.get(step, {}).get("timed_out") is not False
                       for step in ("smoke", "native"))):
            raise ValueError(f"Windows DLL lacks matching successful Proton qualification: {path}")


def validate(root, revision=None):
    for target in ("template_debug", "template_release"):
        for directory, suffix in (("linux-x64", "linux"), ("windows-x64", "windows")):
            extension = "so" if suffix == "linux" else "dll"
            path = root / directory / f"libdirge_native.{suffix}.{target}.x86_64.{extension}"
            if not path.is_file() or not path.stat().st_size:
                raise ValueError(f"missing native library: {path}")
        name = f"libdirge_native.macos.{target}"
        path = root / "macos" / f"{name}.framework" / name
        if not path.is_file():
            raise ValueError(f"missing native library: {path}")
        universal_architectures(path.read_bytes())
    validate_windows_qualification(root, revision)


if __name__ == "__main__":
    try:
        repository = Path(__file__).resolve().parent.parent
        revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repository, text=True).strip()
        validate(repository / "addons/dirge/bin", revision)
    except ValueError as error:
        raise SystemExit(f"package: {error}")
