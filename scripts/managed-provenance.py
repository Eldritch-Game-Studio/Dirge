#!/usr/bin/env python3
"""Write provenance.json for a CI-built dist/dirge-managed-<sha>.zip (schema eldritch.managed-provenance/1).

Records the Dirge commit, the publishing run, the ZIP checksum, and the NecronomiDOM package this commit was qualified
against, copied from dependency-lock.json. Consumers require their NecronomiDOM pin to equal upstream.necronomidom.sha.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]
REPOSITORY = "Eldritch-Game-Studio/Dirge"
BRANCH = "managed/1.0.x"
_spec = importlib.util.spec_from_file_location("install_dependency", Path(__file__).with_name("install-dependency.py"))
dependency = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(dependency)


def provenance(root: Path, package: Path, ref: str, run_id: int, run_attempt: int) -> dict:
    sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    if package.name != f"dirge-managed-{sha}.zip":
        raise ValueError(f"{package.name} is not the managed-host package of {sha}")
    with zipfile.ZipFile(package) as bundle:
        metadata = json.loads(bundle.read("addon-package.json"))
    if metadata.get("revision") != sha or metadata.get("addon") != "dirge" or metadata.get("profile") != "managed-host":
        raise ValueError("addon-package.json does not identify this Dirge managed-host revision")
    if not (ref == "refs/heads/" + BRANCH or ref.startswith("refs/tags/v")):
        raise ValueError(f"managed releases publish only {BRANCH} and v* tags, not {ref}")
    lock = dependency.read_lock(root)
    return {
        "schema": "eldritch.managed-provenance/1",
        "package": "dirge",
        "repository": REPOSITORY,
        "sha": sha,
        "branch": BRANCH,
        "ref": ref,
        "run_id": run_id,
        "run_attempt": run_attempt,
        # Publication runs inside the qualifying run itself (after `qualification` passed), so they are the same run.
        "qualified_run_id": run_id,
        "release_tag": "managed-" + sha[:12],
        "zip": package.name,
        "zip_sha256": hashlib.sha256(package.read_bytes()).hexdigest(),
        "upstream": {"necronomidom": lock},
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("package", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--ref", default=os.environ.get("GITHUB_REF", ""))
    parser.add_argument("--run-id", type=int, default=int(os.environ.get("GITHUB_RUN_ID", "0")))
    parser.add_argument("--run-attempt", type=int, default=int(os.environ.get("GITHUB_RUN_ATTEMPT", "0")))
    args = parser.parse_args()
    if args.run_id <= 0 or args.run_attempt <= 0:
        raise SystemExit("managed-provenance: a CI run id and attempt are required")
    try:
        record = provenance(ROOT, args.package.resolve(), args.ref, args.run_id, args.run_attempt)
    except (OSError, ValueError, KeyError, zipfile.BadZipFile, subprocess.CalledProcessError) as error:
        raise SystemExit(f"managed-provenance: {error}")
    args.output.write_text(json.dumps(record, indent=2, sort_keys=True) + "\n")
    print(json.dumps(record, indent=2, sort_keys=True))
