#!/usr/bin/env python3
"""Install only an explicitly pinned NecronomiDOM runtime ZIP, never a floating release."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import tempfile
import urllib.parse
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PREFIX = "addons/necronomidom/"
FORBIDDEN = {".cs", ".csproj", ".fsproj", ".targets", ".props", ".nupkg", ".snupkg", ".exe", ".binlog", ".pdb", ".sln", ".slnx"}


def identity(root):
    supplied = [os.environ.get("NECRONOMIDOM_" + key) for key in ("PACKAGE_URL", "PACKAGE_SHA256", "REVISION")]
    if any(supplied):
        if not all(supplied):
            raise ValueError("set NECRONOMIDOM_PACKAGE_URL, NECRONOMIDOM_PACKAGE_SHA256 and NECRONOMIDOM_REVISION together")
        pin = dict(zip(("url", "sha256", "revision"), supplied))
    else:
        lock = root / "dependency-lock.json"
        if not lock.is_file():
            raise ValueError("exact NecronomiDOM bridge package required: supply dependency-lock.json or all three NECRONOMIDOM pin variables; no released-version fallback")
        pin = json.loads(lock.read_text())["necronomidom"]
    if not re.fullmatch(r"[0-9a-f]{64}", pin.get("sha256", "")):
        raise ValueError("NecronomiDOM package SHA-256 must be 64 lowercase hex characters")
    if not re.fullmatch(r"[0-9a-f]{40}", pin.get("revision", "")):
        raise ValueError("NecronomiDOM revision must be an exact 40-character commit")
    parsed = urllib.parse.urlparse(pin.get("url", ""))
    if parsed.scheme not in ("https", "file") or (parsed.scheme == "file" and parsed.netloc not in ("", "localhost")):
        raise ValueError("NecronomiDOM package URL must be HTTPS or a local file:// URL")
    if parsed.scheme == "file" and not Path(urllib.request.url2pathname(parsed.path)).is_absolute():
        pin["url"] = (root / urllib.request.url2pathname(parsed.path)).resolve().as_uri()
    return {key: pin[key] for key in ("url", "sha256", "revision")}


def install(root, archive_output=None):
    pin = identity(root)
    addons = root / "addons"
    addons.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".necronomidom-", dir=addons) as directory:
        work = Path(directory)
        archive = work / "dependency.zip"
        request = urllib.request.Request(pin["url"], headers={"User-Agent": "Dirge-dependency-installer"})
        with urllib.request.urlopen(request, timeout=120) as response, archive.open("wb") as output:
            if urllib.parse.urlparse(response.url).scheme != urllib.parse.urlparse(pin["url"]).scheme:
                raise ValueError("dependency download changed URL scheme")
            shutil.copyfileobj(response, output)
        if hashlib.sha256(archive.read_bytes()).hexdigest() != pin["sha256"]:
            raise ValueError("NecronomiDOM package checksum mismatch")
        staged = work / "necronomidom"
        staged.mkdir()
        files = {}
        with zipfile.ZipFile(archive) as bundle:
            names = bundle.namelist()
            if len(names) != len(set(names)):
                raise ValueError("duplicate dependency archive members")
            for member in bundle.infolist():
                name = member.filename
                path = PurePosixPath(name)
                if path.is_absolute() or ".." in path.parts or "\\" in name or stat.S_ISLNK(member.external_attr >> 16):
                    raise ValueError(f"unsafe dependency archive member: {name}")
                if path.suffix.lower() in FORBIDDEN or name.lower().endswith(".cs.uid") or set(path.parts) & {"obj", ".godot", ".git"}:
                    raise ValueError(f"dependency is not a runtime-only package: {name}")
                if member.is_dir() or not name.startswith(PREFIX):
                    continue
                relative = name[len(PREFIX):]
                data = bundle.read(member)
                target = staged / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(data)
                files[relative] = hashlib.sha256(data).hexdigest()
        if not any(name.endswith(".gdextension") for name in files) or not any(name.endswith((".so", ".dll", ".dylib")) for name in files):
            raise ValueError("dependency ZIP lacks the NecronomiDOM native runtime")
        (staged / ".dirge-dependency.json").write_text(json.dumps(dict(pin, files=files), indent=2) + "\n")
        if archive_output:
            archive_output.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(archive, archive_output)
        destination = addons / "necronomidom"
        backup = work / "previous"
        if destination.exists():
            destination.rename(backup)
        try:
            staged.rename(destination)
        except BaseException:
            if backup.exists():
                backup.rename(destination)
            raise
    print(f"fetch-deps: installed NecronomiDOM {pin['revision']} sha256={pin['sha256']}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--archive-output", type=Path, help="retain the exact verified ZIP for installed-package qualification")
    args = parser.parse_args()
    try:
        install(args.root.resolve(), args.archive_output)
    except (OSError, ValueError, KeyError, zipfile.BadZipFile) as error:
        raise SystemExit(f"fetch-deps: {error}")
