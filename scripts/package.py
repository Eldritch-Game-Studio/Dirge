#!/usr/bin/env python3
"""Assemble an addon ZIP without equating structural closure with OS qualification."""
import argparse
import configparser
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("package_native", Path(__file__).with_name("package-native.py"))
native = importlib.util.module_from_spec(spec)
spec.loader.exec_module(native)
DOCUMENTS = ("LICENSE", "README.md", "CHANGELOG.md", "THIRD_PARTY_NOTICES.md")
JUNK = {".git", ".github", ".godot", "obj", "__pycache__", "__MACOSX", ".gitignore", ".gitattributes", ".DS_Store", "Thumbs.db"}


def build(root, suffix="", require_qualification=False):
    if suffix not in ("", "-partial"):
        raise ValueError("only the explicit -partial diagnostic suffix is supported")
    if suffix and require_qualification:
        raise ValueError("partial diagnostics cannot claim full qualification")
    addon = root / "addons/dirge"
    configuration = configparser.ConfigParser(interpolation=None)
    configuration.read_string((addon / "plugin.cfg").read_text())
    version = configuration.get("plugin", "version").strip('"')
    if not version or any(character not in "0123456789.-abcdefghijklmnopqrstuvwxyz" for character in version):
        raise ValueError("invalid plugin version")
    revision = None
    if require_qualification:
        revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
        # A receipt for HEAD cannot qualify edited tracked source at that same HEAD.
        if subprocess.run(["git", "diff", "--quiet", "HEAD", "--"], cwd=root).returncode:
            raise ValueError("current-source qualification requires unchanged tracked source")
    files = {}
    for path in sorted(addon.rglob("*")):
        if not path.is_file():
            continue
        relative = path.relative_to(addon)
        name = relative.as_posix()
        if (set(relative.parts) & JUNK or name.startswith("model/bin/")
                or path.suffix.lower() in native.FORBIDDEN_SUFFIXES
                or name.lower().endswith(".cs.uid") or name.endswith(".import.tmp")
                or name in DOCUMENTS or name == "package-contract.json"
                or (name == "bin/windows-x64/proton-qualification.json" and not require_qualification)):
            continue
        if path.is_symlink():
            raise ValueError(f"addon assets must not be symlinks: {name}")
        files["addons/dirge/" + name] = path.read_bytes()
    for name in DOCUMENTS:
        files["addons/dirge/" + name] = (root / name).read_bytes()
    lock = root / "dependency-lock.json"
    if lock.is_file():
        files["addons/dirge/dependency-lock.json"] = lock.read_bytes()
    hashes = {name: hashlib.sha256(data).hexdigest() for name, data in files.items() if name.startswith("addons/dirge/bin/") and not name.endswith(".json")}
    files["addons/dirge/package-contract.json"] = (json.dumps({
        "schema": 1, "version": version, "godot": "4.7.2", "distribution": "standard",
        "closure": "partial-diagnostic" if suffix else "six-native-gdscript-ui",
        "runtime_evidence": "current-source-windows-proton" if require_qualification else "not-asserted",
        "source_revision": revision, "native_sha256": hashes,
    }, indent=2, sort_keys=True) + "\n").encode()
    destination = root / "dist"
    destination.mkdir(exist_ok=True)
    name = f"Dirge-v{version}{suffix}.zip"
    with tempfile.TemporaryDirectory(prefix=".package.", dir=destination) as directory:
        staged = Path(directory) / name
        with zipfile.ZipFile(staged, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as package:
            for member, data in sorted(files.items()):
                info = zipfile.ZipInfo(member, date_time=(1980, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.create_system = 3
                info.external_attr = 0o100644 << 16
                package.writestr(info, data)
        native.validate_archive(staged, revision, partial=bool(suffix))
        staged.replace(destination / name)
    print(f"package: {destination / name} — {len(files)} files; structural closure validated")
    if not require_qualification:
        print("package: OS runtime qualification is not asserted; native hashes are recorded in package-contract.json")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--suffix", default="")
    parser.add_argument("--require-qualification", action="store_true")
    args = parser.parse_args()
    try:
        build(ROOT, args.suffix, args.require_qualification)
    except (OSError, KeyError, ValueError, configparser.Error, zipfile.BadZipFile, subprocess.CalledProcessError) as error:
        raise SystemExit(f"package: {error}")
