#!/usr/bin/env python3
"""Advance dependency-lock.json to a NecronomiDOM managed-host package published by NecronomiDOM CI.

Accepts an upstream-package-published payload only when the managed-<sha12> release on NecronomiDOM carries a
provenance.json naming exactly that commit and ZIP, the ZIP's checksum, manifest and per-file hashes verify, the
commit is on NecronomiDOM main, and the CI run named by the provenance built that commit. A payload older than the
current lock is ignored (newer versions coalesce; a lock never moves backwards).
Prints and, under GitHub Actions, writes `changed=true|false` and the accepted identity to $GITHUB_OUTPUT.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
_spec = importlib.util.spec_from_file_location("install_dependency", Path(__file__).with_name("install-dependency.py"))
dependency = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(dependency)

PROVENANCE_SCHEMA = "eldritch.managed-provenance/1"
UPSTREAM_BRANCH = "main"


def gh(path: str) -> dict:
    """Read-only GitHub REST call through the runner's gh CLI."""
    return json.loads(subprocess.run(["gh", "api", path], check=True, capture_output=True, text=True).stdout)


def verify_provenance(provenance: dict, pin: dict) -> None:
    expected = {
        "schema": PROVENANCE_SCHEMA,
        "package": dependency.ADDON,
        "repository": pin["repository"],
        "sha": pin["sha"],
        "branch": UPSTREAM_BRANCH,
        "release_tag": pin["release_tag"],
        "zip": pin["zip"],
        "zip_sha256": pin["zip_sha256"],
    }
    for key, value in expected.items():
        if provenance.get(key) != value:
            raise ValueError(f"provenance {key} is {provenance.get(key)!r}, expected {value!r}")
    if not isinstance(provenance.get("run_id"), int) or provenance["run_id"] <= 0:
        raise ValueError("provenance run_id must name the publishing CI run")


def verify_github(provenance: dict, pin: dict, api=gh) -> None:
    repository = pin["repository"]
    compare = api(f"repos/{repository}/compare/{pin['sha']}...{UPSTREAM_BRANCH}")
    if compare["status"] not in ("ahead", "identical"):
        raise ValueError(f"{pin['sha']} is not on {repository} {UPSTREAM_BRANCH} (compare status {compare['status']})")
    # A backfill run publishes an earlier main commit whose own main-push run already qualified it.
    qualified = provenance.get("qualified_run_id")
    run = api(f"repos/{repository}/actions/runs/{qualified or provenance['run_id']}")
    if run["head_sha"] != pin["sha"] or run["repository"]["full_name"] != repository:
        raise ValueError(f"CI run {run['id']} did not build {repository}@{pin['sha']}")
    if qualified and run["conclusion"] != "success":
        raise ValueError(f"qualifying CI run {qualified} concluded {run['conclusion']!r}")
    if run["conclusion"] not in (None, "success"):
        raise ValueError(f"publishing CI run {run['id']} concluded {run['conclusion']!r}")
    release = api(f"repos/{repository}/releases/tags/{pin['release_tag']}")
    assets = {asset["name"]: asset for asset in release["assets"]}
    if pin["zip"] not in assets or "provenance.json" not in assets:
        raise ValueError(f"release {pin['release_tag']} lacks {pin['zip']} or provenance.json")
    digest = assets[pin["zip"]].get("digest")
    if digest is not None and digest != "sha256:" + pin["zip_sha256"]:
        raise ValueError(f"release asset digest {digest} differs from {pin['zip_sha256']}")


def order(current: dict, pin: dict, api=gh) -> str:
    """'newer', 'same' or 'older' for the candidate pin against the current lock."""
    if current["sha"] == pin["sha"]:
        if current != pin:
            raise ValueError("payload names the locked commit with a different package identity")
        return "same"
    status = api(f"repos/{pin['repository']}/compare/{current['sha']}...{pin['sha']}")["status"]
    if status == "ahead":
        return "newer"
    if status == "behind":
        return "older"
    raise ValueError(f"locked {current['sha']} and candidate {pin['sha']} have diverged ({status})")


def update(root: Path, package: str, sha: str, release_tag: str, zip_sha256: str, api=gh, fetch=dependency.download) -> dict:
    if package != dependency.ADDON:
        raise ValueError(f"Dirge managed/1.0.x consumes only {dependency.ADDON}, not {package!r}")
    pin = dependency.check_pin({
        "repository": dependency.REPOSITORY,
        "sha": sha,
        "release_tag": release_tag,
        "zip": f"{dependency.ADDON}-managed-{sha}.zip",
        "zip_sha256": zip_sha256,
    })
    current = dependency.read_lock(root)
    relation = order(current, pin, api)
    if relation != "newer":
        return {"changed": False, "relation": relation, **pin}
    provenance = json.loads(fetch(dependency.release_url(pin, "provenance.json")))
    verify_provenance(provenance, pin)
    verify_github(provenance, pin, api)
    data = fetch(dependency.release_url(pin, pin["zip"]))
    if hashlib.sha256(data).hexdigest() != pin["zip_sha256"]:
        raise ValueError("downloaded NecronomiDOM package checksum differs from the payload")
    dependency.package_files(data, pin["zip_sha256"], pin["sha"])
    dependency.write_lock(root, pin)
    return {"changed": True, "relation": relation, "previous_sha": current["sha"], "run_id": provenance["run_id"], **pin}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--package", required=True)
    parser.add_argument("--sha", required=True)
    parser.add_argument("--release-tag", required=True)
    parser.add_argument("--zip-sha256", required=True)
    args = parser.parse_args()
    try:
        result = update(args.root.resolve(), args.package, args.sha, args.release_tag, args.zip_sha256)
    except (OSError, ValueError, KeyError, json.JSONDecodeError, subprocess.CalledProcessError) as error:
        raise SystemExit(f"update-dependency-lock: {error}")
    print(json.dumps(result, indent=2))
    if "GITHUB_OUTPUT" in os.environ:
        with open(os.environ["GITHUB_OUTPUT"], "a") as output:
            output.write(f"changed={str(result['changed']).lower()}\n")
            for key in ("sha", "release_tag", "zip_sha256", "relation"):
                output.write(f"{key}={result[key]}\n")
            if result["changed"]:
                output.write(f"previous_sha={result['previous_sha']}\nrun_id={result['run_id']}\n")
