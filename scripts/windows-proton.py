#!/usr/bin/env python3
"""Qualify real Windows DLLs with standard Windows Godot under official Proton.

Compatibility coverage only: not native Windows/MSVC or export-template coverage.
No Steam launch, user prefix, system Wine, or synthetic native artifact is used.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parent.parent
TARGETS = ("template_debug", "template_release")


def digest(path, algorithm="sha256"):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, algorithm).hexdigest()


def json_write(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n")


def fetch(url, destination):
    if not url.startswith("https://"):
        raise ValueError("downloads require HTTPS")
    request = urllib.request.Request(url, headers={"User-Agent": "Dirge-Proton-qualification"})
    with urllib.request.urlopen(request, timeout=60) as response, destination.open("wb") as output:
        if not response.url.startswith("https://"):
            raise ValueError("insecure download redirect")
        shutil.copyfileobj(response, output)


def metadata(url, destination):
    fetch(url, destination)
    return json.loads(destination.read_text())


def install_archive(url, expected, algorithm, destination, archive):
    if not re.fullmatch(r"[0-9a-fA-F]{%d}" % (64 if algorithm == "sha256" else 128), expected):
        raise ValueError("upstream release metadata has no valid artifact digest")
    fetch(url, archive)
    actual = digest(archive, algorithm)
    if actual != expected.lower():
        raise ValueError(f"upstream digest mismatch: {archive.name}")
    with zipfile.ZipFile(archive) as bundle:
        for entry in bundle.infolist():
            resolved = (destination / entry.filename).resolve()
            if not resolved.is_relative_to(destination.resolve()):
                raise ValueError("archive contains an escaping path")
        bundle.extractall(destination)
    return {"url": url, algorithm: actual}


def stop_group(process):
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    process.wait()


def interrupted(signum, frame):
    raise InterruptedError(f"qualification interrupted by signal {signum}")


def run(command, log, cwd, env, seconds, check=True):
    """Record actual exit/timeout; bound and stop only this invocation's group."""
    started = time.monotonic()
    timed_out = False
    with log.open("wb") as stream:
        process = subprocess.Popen(command, cwd=cwd, env=env, stdout=stream,
                                   stderr=subprocess.STDOUT, start_new_session=True)
        try:
            status = process.wait(timeout=seconds)
        except subprocess.TimeoutExpired:
            timed_out = True
            stop_group(process)
            status = 124
        except BaseException:
            stop_group(process)
            raise
    result = {"command": command, "exit_code": status, "timed_out": timed_out,
              "elapsed_seconds": round(time.monotonic() - started, 3), "log": str(log)}
    json_write(log.with_suffix(".status.json"), result)
    if check and status:
        raise RuntimeError(f"command failed (exit {status}): {command}; see {log}")
    return result


def quiesce_prefix(proton, work, log, env):
    """Stop only our prefix and prove its server released its lifetime lock.

    Proton 11 server/main.c exits !kill_lock_owner() for -k: an already
    unlocked prefix returns 1. The documented -w waits for that same lock.
    Record both results; never mistake an arbitrary kill error for quiescence.
    """
    cleanup_env = dict(env, WINEPREFIX=str(work / "compat/pfx"))
    server = str(proton / "files/bin/wineserver")
    stopped = run([server, "-k"], log, ROOT, cleanup_env, 15, check=False)
    run([server, "-w"], log.with_suffix(".wait.log"), ROOT, cleanup_env, 15)
    if (stopped["timed_out"] or stopped["exit_code"] not in (0, 1)
            or (stopped["exit_code"] == 1 and log.read_text().strip())):
        raise RuntimeError(f"prefix cleanup failed: {log}")


def validate_engine_log(path):
    if not path.is_file():
        raise RuntimeError(f"Windows Godot did not produce its engine log: {path}")
    if re.search(r"^(?:ERROR:|SCRIPT ERROR:)", path.read_text(errors="replace"), re.MULTILINE):
        raise RuntimeError(f"Windows Godot reported engine/script errors: {path}")


def windows_path(path):
    return "Z:" + str(path.resolve()).replace("/", "\\")


def godot_run(arguments):
    """Called by smoke.sh through a per-run executable wrapper."""
    work = Path(os.environ["DIRGE_PROTON_WORK"])
    proton = Path(os.environ["DIRGE_PROTON_HOME"])
    env = dict(os.environ)
    for key in tuple(env):
        if key.startswith(("WINE", "PROTON_", "STEAM_COMPAT_", "DOTNET_ROOT")) or key in ("SteamAppId", "SteamGameId", "DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY"):
            del env[key]
    env.update({
        "STEAM_COMPAT_DATA_PATH": str(work / "compat"),
        "STEAM_COMPAT_CLIENT_INSTALL_PATH": str(work / "steam"),
        "SteamAppId": "0", "SteamGameId": "0",
        "PROTON_LOG": "1", "PROTON_LOG_DIR": str(work / "logs"),
    })
    if "--path" in arguments:
        index = arguments.index("--path") + 1
        arguments[index] = windows_path(Path(arguments[index]))
    log = work / "logs" / f"godot-{time.time_ns()}.log"
    engine_log = log.with_suffix(".engine.log")
    version_output = log.with_suffix(".version.txt")
    if arguments == ["--version"]:
        # Proton does not forward a console program's stdout, and --version exits
        # before --log-file opens. Let cmd.exe redirect the engine's real output.
        script = log.with_suffix(".version.cmd")
        script.write_text(f'@"{windows_path(Path(os.environ["DIRGE_WINDOWS_GODOT"]))}" --version > "{windows_path(version_output)}"\r\n')
        command = ["C:\\windows\\system32\\cmd.exe", "/c", windows_path(script)]
    else:
        command = [os.environ["DIRGE_WINDOWS_GODOT"], "--log-file", windows_path(engine_log), *arguments]
    primary_error = None
    try:
        run(["xvfb-run", "--auto-servernum", "--error-file", str(log.with_suffix(".xvfb.log")),
             "--server-args=-screen 0 1280x720x24 -nolisten tcp",
             str(proton / "proton"), "waitforexitandrun", *command],
            log, Path.cwd(), env, 180)
        if arguments != ["--version"]:
            validate_engine_log(engine_log)
    except BaseException as error:
        primary_error = error
        raise
    finally:
        if arguments == ["--version"]:
            # Keep stdout to the engine's own answer; Proton diagnostics go to stderr.
            print(log.read_text(errors="replace"), end="", file=sys.stderr, flush=True)
            if version_output.is_file():
                print(version_output.read_text(errors="replace").replace("\r\n", "\n"), end="", flush=True)
        else:
            print(log.read_text(errors="replace"), end="", flush=True)
        if engine_log.is_file():
            print(engine_log.read_text(errors="replace"), end="", flush=True)
        try:
            quiesce_prefix(proton, work, log.with_suffix(".cleanup.log"), env)
        except BaseException as cleanup_error:
            if primary_error is None:
                raise
            print(f"Additional prefix cleanup failure: {cleanup_error}", file=sys.stderr)
        proton_log = work / "logs/steam-0.log"
        if proton_log.exists():
            proton_log.rename(log.with_suffix(".proton.log"))


def stage_source(destination):
    names = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT).decode().split("\0")
    source_hash = hashlib.sha256()
    for name in sorted(filter(None, names)):
        source = ROOT / name
        if "__pycache__" in Path(name).parts or name == "native/godot-cpp":
            continue
        if not source.is_file():
            raise ValueError(f"tracked source unavailable: {name}")
        source_hash.update(name.encode() + b"\0" + source.read_bytes() + b"\0")
        target = destination / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
    return source_hash.hexdigest()


def qualify():
    if sys.platform != "linux":
        raise ValueError("Proton qualification requires Linux")
    version = os.environ.get("GODOT_VERSION", "4.7.2")
    if version != "4.7.2":
        raise ValueError("standard Godot 4.7.2 is required")
    proton = Path(os.environ.get("PROTON_HOME", str(Path.home() / ".local/share/Steam/steamapps/common/Proton 11.0"))).resolve()
    proton_version = (proton / "version").read_text().strip()
    if not re.fullmatch(r"[0-9]+ proton-11\.[^ ]+-x86_64", proton_version) or "GE" in proton_version:
        raise ValueError("official Proton 11 is required; no Wine/GE fallback")
    if not (proton / "files/share/default_pfx").is_dir():
        raise ValueError("official Proton installation is incomplete (default prefix missing)")
    binary_root = ROOT / "addons/dirge/bin/windows-x64"
    receipt_path = binary_root / "proton-qualification.json"
    receipt_path.unlink(missing_ok=True)
    hashes = {target: digest(binary_root / f"libdirge_native.windows.{target}.x86_64.dll") for target in TARGETS}
    temp_root = Path(os.environ.get("RUNNER_TEMP", tempfile.gettempdir()))
    work = Path(tempfile.mkdtemp(prefix="dirge-proton-", dir=temp_root)).resolve()
    log_root = Path(os.environ.get("DIRGE_PROTON_LOG_DIR", str(work / "logs"))).resolve()
    if "DIRGE_PROTON_LOG_DIR" in os.environ:
        log_root /= work.name
    log_root.mkdir(parents=True, exist_ok=True)
    # Keep the unique run directory in logs even when setup fails. No stale PASS
    # receipt survives a new invocation; only all-success writes qualification.
    report = {"schema": 2, "coverage": "windows-x64-proton-compatibility", "godot_distribution": "standard",
              "source_revision": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
              "workspace": str(work), "godot_version": version,
              "runner_sha256": digest(Path(__file__).resolve()),
              "proton": {"path": str(proton), "version": proton_version,
                         "sha256": {name: digest(proton / name) for name in ("proton", "files/bin/wine", "files/bin/wineserver")}},
              "targets": {}}
    try:
        release = metadata(f"https://api.github.com/repos/godotengine/godot/releases/tags/{version}-stable", work / "godot-release.json")
        archive_name = f"Godot_v{version}-stable_win64.exe.zip"
        asset = next(item for item in release["assets"] if item["name"] == archive_name)
        expected_url = f"https://github.com/godotengine/godot/releases/download/{version}-stable/{archive_name}"
        if asset["browser_download_url"] != expected_url or not (asset.get("digest") or "").startswith("sha256:"):
            raise ValueError("matching stock Godot archive lacks authenticated upstream SHA-256 identity")
        report["godot"] = install_archive(expected_url, asset["digest"][7:], "sha256", work / "godot", work / archive_name)
        executables = list((work / "godot").rglob(f"Godot_v{version}-stable_win64_console.exe"))
        if len(executables) != 1:
            raise ValueError("standard Windows Godot console executable missing or ambiguous")
        executable = executables[0]
        report["godot"]["executable_sha256"] = digest(executable)
        for name in ("compat", "steam", "logs"):
            (work / name).mkdir(exist_ok=True)
        wrapper = work / "godot-proton"
        wrapper.write_text(f"#!/bin/sh\nexec {shlex.quote(sys.executable)} {shlex.quote(str(Path(__file__).resolve()))} --godot \"$@\"\n")
        wrapper.chmod(0o700)
        env = dict(os.environ, GODOT=str(wrapper), DIRGE_PROTON_WORK=str(work),
                   DIRGE_PROTON_HOME=str(proton), DIRGE_WINDOWS_GODOT=str(executable))
        env.pop("GODOT_ARCH", None)
        for target in TARGETS:
            stage = work / target
            stage.mkdir()
            source_hash = stage_source(stage)
            report["source_content_sha256"] = source_hash
            staged_binary = stage / "addons/dirge/bin/windows-x64" / f"libdirge_native.windows.{target}.x86_64.dll"
            staged_binary.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(binary_root / staged_binary.name, staged_binary)
            if digest(staged_binary) != hashes[target]:
                raise ValueError("DLL changed during staging")
            # The stock editor is a debug-feature host. Select the release DLL
            # explicitly in the disposable mapping, without renaming DLL bytes.
            mapping = stage / "addons/dirge/dirge_native.gdextension"
            mapping.write_text(mapping.read_text().replace(
                "windows.template_debug.x86_64.dll", f"windows.{target}.x86_64.dll"))
            logs = work / "logs" / target
            logs.mkdir()
            target_env = dict(env, DIRGE_SMOKE_LOG_DIR=str(logs))
            settings = (stage / "project.godot").read_bytes()
            smoke = run(["bash", "scripts/smoke.sh"], logs / "smoke.log", stage, target_env, 900)
            if (stage / "project.godot").read_bytes() != settings:
                raise ValueError("smoke import rewrote project.godot")
            native = run([str(wrapper), "--headless", "--audio-driver", "Dummy", "--path", ".",
                          "res://native/tests/sendreturn.tscn"], logs / "native.log", stage, target_env, 240)
            for filename, marker in (("gdscript.log", "DIRGE_TEST PASS"), ("demo.log", "DIRGE_SMOKE PASS"), ("sandbox.log", "SANDBOX_SMOKE PASS"), ("native.log", "NATIVE_TEST PASS")):
                if not any(line == marker or line.startswith(marker + " ") for line in (logs / filename).read_text(errors="replace").splitlines()):
                    raise ValueError(f"{target}: missing {marker}")
            if digest(staged_binary) != hashes[target] or digest(binary_root / staged_binary.name) != hashes[target]:
                raise ValueError("qualified DLL changed during smoke")
            report["targets"][target] = {"sha256": hashes[target], "status": "PASS",
                "host": "stock-windows-standard-editor", "selection": "staged-debug-feature-mapping",
                "assertions": ["DIRGE_TEST PASS", "DIRGE_SMOKE PASS", "SANDBOX_SMOKE PASS", "NATIVE_TEST PASS"],
                "smoke": smoke, "native": native}
        report["status"] = "PASS"
        json_write(receipt_path, report)
        print(f"Proton compatibility PASS (not native Windows/MSVC): {receipt_path}")
    except BaseException as error:
        report["status"] = "FAIL"
        report["error"] = str(error)
        receipt_path.unlink(missing_ok=True)
        if (work / "compat/pfx").is_dir():
            try:
                quiesce_prefix(proton, work, log_root / "failure-cleanup.log", os.environ)
            except BaseException as cleanup_error:
                report["cleanup_error"] = str(cleanup_error)
                print(f"Additional prefix cleanup failure: {cleanup_error}", file=sys.stderr)
        raise
    finally:
        json_write(work / "qualification.json", report)
        if log_root != work / "logs" and (work / "logs").exists():
            shutil.copytree(work / "logs", log_root, dirs_exist_ok=True)
        shutil.copy2(work / "qualification.json", log_root / "qualification.json")
        for name in ("godot-release.json",):
            if (work / name).exists():
                shutil.copy2(work / name, log_root / name)
        print(f"Proton qualification evidence: {log_root}", flush=True)


if __name__ == "__main__":
    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    try:
        if sys.argv[1:2] == ["--godot"]:
            godot_run(sys.argv[2:])
        else:
            qualify()
    except (OSError, ValueError, RuntimeError, StopIteration) as error:
        raise SystemExit(f"windows-proton: {error}")
