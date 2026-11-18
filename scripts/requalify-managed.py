#!/usr/bin/env python3
"""Prepare append-only evidence from an independently successful FULL managed-line CI run.

Never rebuild, publish, replace provenance.json, or treat its original missing run as passing.
The qualifying run's managed artifact must reproduce the released ZIP byte for byte.

Maintainers dispatch ci.yml on managed/1.0.x with requalification_run=<successful run ID>.
The dedicated read-only preparation and append-only publication jobs schedule no native or
managed rebuilds. They append provenance-<publisher run ID>-<publisher attempt>.json using
schema eldritch.managed-requalification/1. run_id identifies that recovery publisher, not
the unavailable original package build; qualified_run_id identifies the independent FULL
run that produced the byte-identical managed ZIP. original_provenance binds the untouched
provenance.json by release asset ID and raw-byte SHA256. qualified_artifact binds the new
run's actual managed artifact. Package/source/NecronomiDOM identity must all be unchanged.

Consumers must explicitly pin the appended asset name and receipt SHA256 in their lock,
retain the original provenance and package checks, authenticate this publishing run plus
the exact-attempt FULL qualifying run, and never reinterpret the missing original CI as
successful. There is no fallback, latest-asset discovery, or automatic consumer dispatch.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import re
import subprocess
import tarfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
REPOSITORY = "Eldritch-Game-Studio/Dirge"
BRANCH = "managed/1.0.x"
WORKFLOW = ".github/workflows/ci.yml"
REQUIRED_JOBS = (
    "local / trust", "local / test", "local / validate", "local / smoke",
    "local / native-linux", "local / Windows x64 cross-build + Proton compatibility",
    "local / native-macos", "local / package", "local / managed-package", "local / qualification",
)


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def gh(path: str, binary: bool = False):
    command = ["gh", "api", path]
    if binary and "/releases/assets/" in path:
        command += ["-H", "Accept: application/octet-stream"]
    data = subprocess.check_output(command)
    return data if binary else json.loads(data)


def pages(path: str, key: str, api=gh) -> list:
    result = []
    page = 1
    while True:
        items = api(f"{path}?per_page=100&page={page}")[key]
        result.extend(items)
        if len(items) < 100:
            return result
        page += 1


def verify_run(run: dict, jobs: list, run_id: int) -> str:
    sha = run.get("head_sha", "")
    require(type(run_id) is int and run_id > 0 and run.get("id") == run_id, "Invalid qualifying run ID")
    require(re.fullmatch(r"[0-9a-f]{40}", sha) is not None, "Invalid exact source revision")
    require(run.get("repository", {}).get("full_name") == REPOSITORY and run.get("path") == WORKFLOW
            and run.get("head_branch") == BRANCH and run.get("event") in ("push", "workflow_dispatch"),
            "Qualification must be the maintained CI workflow on managed/1.0.x")
    require(run.get("status") == "completed" and run.get("conclusion") == "success",
            "Independent qualification has not completed successfully")
    attempt = run.get("run_attempt")
    require(type(attempt) is int and attempt > 0, "Invalid qualifying attempt")
    for name in REQUIRED_JOBS:
        matches = [job for job in jobs if job.get("name") == name]
        require(len(matches) == 1 and matches[0].get("status") == "completed"
                and matches[0].get("conclusion") == "success" and matches[0].get("run_attempt") == attempt,
                f"FULL exact-attempt qualification requires successful {name}")
    return sha


def verify_package(data: bytes, sha: str, source_files: dict[str, bytes]) -> None:
    with zipfile.ZipFile(io.BytesIO(data)) as bundle:
        metadata = json.loads(bundle.read("addon-package.json"))
        require(all(metadata.get(key) == value for key, value in {
            "schema": "eldritch.managed-addon/1", "addon": "dirge", "revision": sha,
            "repository": "https://github.com/" + REPOSITORY, "profile": "managed-host",
        }.items()), "Package manifest does not identify the exact managed-host source")
        expected = {"addons/dirge/" + name: content for name, content in source_files.items()}
        names = bundle.namelist()
        require(len(names) == len(set(names)) and set(names) == set(expected) | {"addon-package.json"},
                "Package archive members differ from exact source")
        hashes = {name: digest(content) for name, content in source_files.items()}
        require(metadata.get("files") == hashes, "Package manifest differs from exact source tree")
        require(all(bundle.read(name) == content for name, content in expected.items()),
                "Package bytes differ from exact source tree")


def asset(release: dict, name: str) -> dict:
    matches = [item for item in release["assets"] if item["name"] == name]
    require(len(matches) == 1, f"Release must contain exactly one {name}")
    return matches[0]


def prepare(run_id: int, producer_id: int, producer_attempt: int, root: Path = ROOT, api=gh) -> dict:
    prefix = f"repos/{REPOSITORY}"
    run = api(f"{prefix}/actions/runs/{run_id}")
    attempt = run.get("run_attempt")
    jobs = pages(f"{prefix}/actions/runs/{run_id}/attempts/{attempt}/jobs", "jobs", api)
    sha = verify_run(run, jobs, run_id)
    require(producer_id > 0 and producer_attempt > 0 and producer_id != run_id,
            "Recovery publisher and original exact-source qualification must be distinct runs")
    subprocess.run(["git", "merge-base", "--is-ancestor", sha, f"origin/{BRANCH}"], cwd=root, check=True)
    lock = json.loads(subprocess.check_output(["git", "show", f"{sha}:dependency-lock.json"], cwd=root))
    tag, name = "managed-" + sha[:12], f"dirge-managed-{sha}.zip"
    release = api(f"{prefix}/releases/tags/{tag}")
    require(release.get("tag_name") == tag and not release.get("draft"), "Unexpected or draft managed release")
    original_asset, zip_asset = asset(release, "provenance.json"), asset(release, name)
    original_bytes = api(f"{prefix}/releases/assets/{original_asset['id']}", True)
    original = json.loads(original_bytes)
    package = api(f"{prefix}/releases/assets/{zip_asset['id']}", True)
    checksum = digest(package)
    identity = {
        "package": "dirge", "repository": REPOSITORY, "branch": BRANCH, "sha": sha,
        "release_tag": tag, "zip": name, "zip_sha256": checksum, "upstream": lock,
    }
    require(original.get("schema") == "eldritch.managed-provenance/1"
            and all(original.get(key) == value for key, value in identity.items()),
            "Original provenance identity, package or locked NecronomiDOM differs")
    require(original.get("ref") == "refs/heads/" + BRANCH
            and all(type(original.get(key)) is int and original[key] > 0
                    for key in ("run_id", "run_attempt", "qualified_run_id"))
            and original["run_id"] == original["qualified_run_id"],
            "Original provenance does not identify its original managed-line producer")
    require(original_asset.get("digest") == "sha256:" + digest(original_bytes)
            and zip_asset.get("digest") == "sha256:" + checksum, "Release asset digests differ")
    artifacts = pages(f"{prefix}/actions/runs/{run_id}/artifacts", "artifacts", api)
    matches = [item for item in artifacts if item.get("name") == "dirge-managed"]
    require(len(matches) == 1, "Qualification must have exactly one dirge-managed artifact")
    artifact = matches[0]
    require(not artifact.get("expired") and artifact.get("workflow_run", {}).get("id") == run_id
            and artifact["workflow_run"].get("head_sha") == sha
            and artifact["workflow_run"].get("head_branch") == BRANCH, "Managed artifact source differs or expired")
    archive_bytes = api(f"{prefix}/actions/artifacts/{artifact['id']}/zip", True)
    require(artifact.get("digest") == "sha256:" + digest(archive_bytes), "Managed artifact archive digest differs")
    with zipfile.ZipFile(io.BytesIO(archive_bytes)) as archive:
        require(archive.namelist().count(name) == 1, "Managed artifact lacks exactly one expected ZIP")
        require(archive.read(name) == package, "Newly qualified ZIP is not byte-identical to released ZIP")
    source_files = {}
    source = subprocess.check_output(["git", "archive", sha, "addons/dirge"], cwd=root)
    with tarfile.open(fileobj=io.BytesIO(source)) as archive:
        for member in archive:
            if member.isdir():
                continue
            require(member.isfile() and member.name.startswith("addons/dirge/"), "Unsupported source member")
            name_in_addon = member.name.removeprefix("addons/dirge/")
            if not name_in_addon.endswith((".gdextension", ".gdextension.uid", ".import")):
                source_files[name_in_addon] = archive.extractfile(member).read()
    verify_package(package, sha, source_files)
    # Detect reruns or qualification changes while downloading and validating the packages.
    require(api(f"{prefix}/actions/runs/{run_id}") == run, "Qualification changed during evidence preparation")
    return {
        "schema": "eldritch.managed-requalification/1", **identity,
        "evidence_kind": "independent-full-qualification-and-identical-managed-zip",
        "run_id": producer_id, "run_attempt": producer_attempt, "ref": "refs/heads/" + BRANCH,
        "qualified_run_id": run_id, "qualified_run_attempt": attempt,
        "original_provenance": {"asset": "provenance.json", "asset_id": original_asset["id"],
                                "sha256": digest(original_bytes)},
        "qualified_artifact": {"id": artifact["id"], "name": "dirge-managed", "sha256": digest(archive_bytes)},
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--qualified-run-id", type=int, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    require(os.environ.get("GITHUB_ACTIONS") == "true"
            and os.environ.get("GITHUB_REPOSITORY") == REPOSITORY
            and os.environ.get("GITHUB_EVENT_NAME") == "workflow_dispatch"
            and os.environ.get("GITHUB_REF") == "refs/heads/" + BRANCH
            and os.environ.get("GITHUB_WORKFLOW_REF") == f"{REPOSITORY}/{WORKFLOW}@refs/heads/{BRANCH}",
            "Evidence must be generated by maintained-branch CI, not a local invocation")
    record = prepare(args.qualified_run_id, int(os.environ["GITHUB_RUN_ID"]), int(os.environ["GITHUB_RUN_ATTEMPT"]))
    args.output.mkdir(parents=True, exist_ok=True)
    output = args.output / f"provenance-{record['run_id']}-{record['run_attempt']}.json"
    output.write_text(json.dumps(record, indent=2, sort_keys=True) + "\n")
    print(f"{output.name} sha256:{digest(output.read_bytes())}")


if __name__ == "__main__":
    main()
