#!/usr/bin/env python3
"""Reject incomplete standalone packages, including thin macOS libraries."""
from pathlib import Path
import struct
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


def validate(root):
    for target in ("template_debug", "template_release"):
        for directory, suffix in (("linux-x64", "linux"), ("windows-x64", "windows")):
            extension = "so" if suffix == "linux" else "dll"
            path = root / directory / f"libdirge_native.{suffix}.{target}.x86_64.{extension}"
            if not path.is_file() or not path.stat().st_size:
                raise ValueError(f"missing native library: {path}")
        name = f"libdirge_native.macos.{target}"
        path = root / "macos" / f"{name}.framework" / f"{name}.dylib"
        if not path.is_file():
            raise ValueError(f"missing native library: {path}")
        universal_architectures(path.read_bytes())


if __name__ == "__main__":
    try:
        validate(Path(__file__).resolve().parent.parent / "addons/dirge/bin")
    except ValueError as error:
        raise SystemExit(f"package: {error}")
