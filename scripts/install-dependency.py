#!/usr/bin/env python3
"""Install the exact NecronomiDOM managed-host ZIP pinned by dependency-lock.json, never a floating release."""
from __future__ import annotations

import argparse
import hashlib
import io
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
ADDON = "necronomidom"
PREFIX = f"addons/{ADDON}/"
METADATA = "addon-package.json"
MARKER = ".dirge-dependency.json"
REPOSITORY = "Eldritch-Game-Studio/NecronomiDOM"
LOCK_KEYS = ("repository", "sha", "release_tag", "zip", "zip_sha256")


def check_pin(pin: dict) -> dict:
    """A lock entry: the CI-published managed-<sha12> release asset of one NecronomiDOM main commit."""
    if set(pin) != set(LOCK_KEYS):
        raise ValueError(f"dependency lock entry must have exactly {', '.join(LOCK_KEYS)}")
    if pin["repository"] != REPOSITORY:
        raise ValueError(f"NecronomiDOM must come from {REPOSITORY}")
    if not re.fullmatch(r"[0-9a-f]{40}", pin["sha"]):
        raise ValueError("NecronomiDOM sha must be an exact 40-character commit")
    if not re.fullmatch(r"[0-9a-f]{64}", pin["zip_sha256"]):
        raise ValueError("NecronomiDOM zip_sha256 must be 64 lowercase hex characters")
    if pin["release_tag"] != "managed-" + pin["sha"][:12]:
        raise ValueError("NecronomiDOM release_tag must be managed-<sha12> of the locked sha")
    if pin["zip"] != f"{ADDON}-managed-{pin['sha']}.zip":
        raise ValueError("NecronomiDOM zip must be the package-managed.py name of the locked sha")
    return pin


def release_url(pin: dict, asset: str) -> str:
    return f"https://github.com/{pin['repository']}/releases/download/{pin['release_tag']}/{asset}"


def read_lock(root: Path) -> dict:
    lock = root / "dependency-lock.json"
    if not lock.is_file():
        raise ValueError("dependency-lock.json is required; there is no released-version fallback")
    return check_pin(json.loads(lock.read_text())[ADDON])


def write_lock(root: Path, pin: dict) -> None:
    (root / "dependency-lock.json").write_text(json.dumps({ADDON: check_pin(pin)}, indent=2) + "\n")


def source(root: Path) -> tuple[str, str, str]:
    """(url, sha256, revision): the lock, or a candidate supplied as three environment variables together."""
    supplied = [os.environ.get("NECRONOMIDOM_" + key, "") for key in ("PACKAGE_URL", "PACKAGE_SHA256", "REVISION")]
    if any(supplied):
        if not all(supplied):
            raise ValueError("set NECRONOMIDOM_PACKAGE_URL, NECRONOMIDOM_PACKAGE_SHA256 and NECRONOMIDOM_REVISION together")
        url, sha256, revision = supplied
        if not re.fullmatch(r"[0-9a-f]{64}", sha256) or not re.fullmatch(r"[0-9a-f]{40}", revision):
            raise ValueError("candidate NecronomiDOM needs a 64-hex SHA-256 and an exact 40-character revision")
    else:
        pin = read_lock(root)
        url, sha256, revision = release_url(pin, pin["zip"]), pin["zip_sha256"], pin["sha"]
    parsed = urllib.parse.urlparse(url)
    if parsed.scheme not in ("https", "file") or (parsed.scheme == "file" and parsed.netloc not in ("", "localhost")):
        raise ValueError("NecronomiDOM package URL must be HTTPS or a local file:// URL")
    if parsed.scheme == "file" and not Path(urllib.request.url2pathname(parsed.path)).is_absolute():
        url = (root / urllib.request.url2pathname(parsed.path)).resolve().as_uri()
    return url, sha256, revision


def download(url: str) -> bytes:
    request = urllib.request.Request(url, headers={"User-Agent": "Dirge-dependency-installer"})
    with urllib.request.urlopen(request, timeout=120) as response:
        if urllib.parse.urlparse(response.url).scheme != urllib.parse.urlparse(url).scheme:
            raise ValueError("dependency download changed URL scheme")
        return response.read()


def package_files(data: bytes, sha256: str, revision: str) -> dict[str, bytes]:
    """Verified addon files of one managed-host ZIP: checksum, manifest identity, and every per-file hash."""
    if hashlib.sha256(data).hexdigest() != sha256:
        raise ValueError("NecronomiDOM package checksum mismatch")
    files: dict[str, bytes] = {}
    with zipfile.ZipFile(io.BytesIO(data)) as bundle:
        names = bundle.namelist()
        if len(names) != len(set(names)):
            raise ValueError("duplicate dependency archive members")
        if METADATA not in names:
            raise ValueError(f"dependency ZIP lacks {METADATA}; only managed-host packages are accepted")
        metadata = json.loads(bundle.read(METADATA))
        for member in bundle.infolist():
            name = member.filename
            path = PurePosixPath(name)
            if path.is_absolute() or ".." in path.parts or "\\" in name or stat.S_ISLNK(member.external_attr >> 16):
                raise ValueError(f"unsafe dependency archive member: {name}")
            if member.is_dir() or name == METADATA:
                continue
            if not name.startswith(PREFIX):
                raise ValueError(f"dependency archive member outside {PREFIX}: {name}")
            files[name[len(PREFIX):]] = bundle.read(member)
    expected = {
        "schema": "eldritch.managed-addon/1",
        "addon": ADDON,
        "repository": "https://github.com/" + REPOSITORY,
        "revision": revision,
        "profile": "managed-host",
    }
    for key, value in expected.items():
        if metadata.get(key) != value:
            raise ValueError(f"{METADATA} {key} is {metadata.get(key)!r}, expected {value!r}")
    hashes = {name: hashlib.sha256(content).hexdigest() for name, content in files.items()}
    if metadata.get("files") != hashes:
        raise ValueError(f"{METADATA} file manifest does not match the archive contents")
    if "NecronomiDom.targets" not in files:
        raise ValueError("dependency ZIP lacks addons/necronomidom/NecronomiDom.targets")
    return files


def installed(root: Path) -> dict | None:
    marker = root / "addons" / ADDON / MARKER
    return json.loads(marker.read_text()) if marker.is_file() else None


def install(root: Path) -> None:
    url, sha256, revision = source(root)
    identity = {"url": url, "sha256": sha256, "revision": revision}
    destination = root / "addons" / ADDON
    current = installed(root)
    if current is not None and {key: current.get(key) for key in identity} == identity and all(
        (destination / name).is_file() and hashlib.sha256((destination / name).read_bytes()).hexdigest() == digest
        for name, digest in current.get("files", {}).items()
    ):
        print(f"fetch-deps: NecronomiDOM {revision} already installed")
        return
    files = package_files(download(url), sha256, revision)
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".necronomidom-", dir=destination.parent) as directory:
        work = Path(directory)
        staged = work / ADDON
        for name, content in files.items():
            target = staged / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content)
        hashes = {name: hashlib.sha256(content).hexdigest() for name, content in sorted(files.items())}
        (staged / MARKER).write_text(json.dumps(dict(identity, files=hashes), indent=2) + "\n")
        backup = work / "previous"
        if destination.exists():
            destination.rename(backup)
        try:
            staged.rename(destination)
        except BaseException:
            if backup.exists():
                backup.rename(destination)
            raise
    print(f"fetch-deps: installed NecronomiDOM {revision} sha256={sha256} ({len(files)} files)")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=ROOT)
    args = parser.parse_args()
    try:
        install(args.root.resolve())
    except (OSError, ValueError, KeyError, json.JSONDecodeError, zipfile.BadZipFile) as error:
        raise SystemExit(f"fetch-deps: {error}")
