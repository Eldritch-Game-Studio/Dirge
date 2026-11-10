#!/usr/bin/env python3
"""Build a commit-pinned source addon for a .NET host that supplies native integration."""
from __future__ import annotations

import hashlib
import io
import json
from pathlib import Path
import subprocess
import tarfile
import zipfile

ROOT = Path(__file__).resolve().parent.parent
ADDON = "dirge"
REPOSITORY = "https://github.com/Eldritch-Game-Studio/Dirge"
PROJECT = "Dirge.Demo.csproj"


def git(*args: str) -> bytes:
    return subprocess.check_output(["git", *args], cwd=ROOT)


def main() -> None:
    if git("status", "--porcelain", "--untracked-files=normal").strip():
        raise SystemExit("Commit upstream changes before packaging; packages identify an exact source revision.")
    revision = git("rev-parse", "HEAD").decode().strip()
    subprocess.run(["dotnet", "build", PROJECT, "-c", "Release", "-m:2", "-t:Rebuild", "-nologo"], cwd=ROOT, check=True)
    if git("status", "--porcelain", "--untracked-files=normal").strip() or git("rev-parse", "HEAD").decode().strip() != revision:
        raise SystemExit("Upstream changed during the build; no package was published.")
    prefix = f"addons/{ADDON}/"
    files: dict[str, bytes] = {}
    with tarfile.open(fileobj=io.BytesIO(git("archive", revision, f"addons/{ADDON}"))) as archive:
        for member in archive:
            if member.isdir():
                continue
            if not member.isfile() or not member.name.startswith(prefix):
                raise SystemExit(f"Unsupported addon archive member: {member.name}")
            relative = member.name[len(prefix):]
            # Native registration and generated asset-import settings belong to the host.
            if relative.endswith((".gdextension", ".gdextension.uid", ".import")):
                continue
            with archive.extractfile(member) as source:
                files[relative] = source.read()
    metadata = {
        "schema": "eldritch.managed-addon/1",
        "addon": ADDON,
        "repository": REPOSITORY,
        "revision": revision,
        "profile": "managed-host",
        "files": {name: hashlib.sha256(data).hexdigest() for name, data in sorted(files.items())},
    }
    output = ROOT / "dist" / f"{ADDON}-managed-{revision}.zip"
    output.parent.mkdir(exist_ok=True)
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as package:
        entries = {prefix + name: data for name, data in files.items()}
        entries["addon-package.json"] = (json.dumps(metadata, indent=2, sort_keys=True) + "\n").encode()
        for name, data in sorted(entries.items()):
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            package.writestr(info, data)
    print(f"{output}\nsha256={hashlib.sha256(output.read_bytes()).hexdigest()}")


if __name__ == "__main__":
    main()
