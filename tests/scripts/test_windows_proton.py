import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[2] / "scripts/windows-proton.py"
spec = importlib.util.spec_from_file_location("windows_proton", SCRIPT)
windows_proton = importlib.util.module_from_spec(spec)
spec.loader.exec_module(windows_proton)


class WindowsProtonTests(unittest.TestCase):
    def test_engine_errors_fail_even_alongside_pass_marker(self):
        with tempfile.TemporaryDirectory() as directory:
            log = Path(directory) / "engine.log"
            for error in ("ERROR: .NET Sdk not found", "SCRIPT ERROR: invalid call"):
                log.write_text(f"{error}\nDIRGE_SMOKE PASS\n")
                with self.assertRaisesRegex(RuntimeError, "engine/script errors"):
                    windows_proton.validate_engine_log(log)

    def test_missing_engine_log_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(RuntimeError, "did not produce"):
                windows_proton.validate_engine_log(Path(directory) / "absent.log")

    def test_clean_engine_log_passes(self):
        with tempfile.TemporaryDirectory() as directory:
            log = Path(directory) / "engine.log"
            log.write_text("Godot Engine\nDIRGE_SMOKE PASS\n")
            windows_proton.validate_engine_log(log)

    def test_cleanup_rejects_failed_quiescence_or_diagnostic(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            log = root / "cleanup.log"
            log.write_text("")
            with patch.object(windows_proton, "run", side_effect=[
                {"exit_code": 1, "timed_out": False}, RuntimeError("wait timed out"),
            ]):
                with self.assertRaisesRegex(RuntimeError, "wait timed out"):
                    windows_proton.quiesce_prefix(root, root, log, {})
            log.write_text("wineserver: permission denied\n")
            with patch.object(windows_proton, "run", side_effect=[
                {"exit_code": 1, "timed_out": False},
                {"exit_code": 0, "timed_out": False},
            ]):
                with self.assertRaisesRegex(RuntimeError, "prefix cleanup failed"):
                    windows_proton.quiesce_prefix(root, root, log, {})

    @staticmethod
    def _prefix(work, outside):
        """What Proton leaves behind: drive links, including one to a directory outside the workspace."""
        prefix = work / "compat/pfx"
        locked = prefix / "drive_c/windows/locked"
        locked.mkdir(parents=True)
        (locked / "system.ini").write_text("[boot]\n")
        locked.chmod(0o500)
        (prefix / "dosdevices").mkdir()
        (prefix / "dosdevices/c:").symlink_to("../drive_c", target_is_directory=True)
        (prefix / "dosdevices/z:").symlink_to(outside, target_is_directory=True)

    def test_workspace_removal_unlinks_drive_links_without_following_them(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            outside = root / "outside"
            outside.mkdir()
            (outside / "keep.txt").write_text("not ours")
            work = root / "dirge-proton-test"
            self._prefix(work, outside)
            windows_proton.remove_workspace(work)
            self.assertFalse(os.path.lexists(work))
            self.assertEqual((outside / "keep.txt").read_text(), "not ours")

    def _qualify(self, root, fail=False, removal_error=None):
        """Run qualify() against fake Proton, Godot and repository fixtures; return (outcome, work, evidence, receipt)."""
        repository = root / "repository"
        (repository / "addons/dirge/bin/windows-x64").mkdir(parents=True)
        (repository / "project.godot").write_text("; fixture\n")
        (repository / "Dirge.Demo.csproj").write_text('<Project Sdk="Godot.NET.Sdk/4.7.2"></Project>\n')
        (repository / "addons/dirge/dirge_native.gdextension").write_text('windows.debug.x86_64 = "windows.template_debug.x86_64.dll"\n')
        for target in windows_proton.TARGETS:
            (repository / f"addons/dirge/bin/windows-x64/libdirge_native.windows.{target}.x86_64.dll").write_bytes(target.encode())
        git = ["git", "-c", "user.name=fixture", "-c", "user.email=fixture@example.invalid"]
        subprocess.run(["git", "init", "-q", str(repository)], check=True)
        subprocess.run([*git, "add", "project.godot", "Dirge.Demo.csproj", "addons/dirge/dirge_native.gdextension"], cwd=repository, check=True)
        subprocess.run([*git, "commit", "-q", "-m", "fixture"], cwd=repository, check=True)
        proton = root / "proton"
        (proton / "files/share/default_pfx").mkdir(parents=True)
        (proton / "files/bin").mkdir()
        (proton / "version").write_text("1762000000 proton-11.0-1-x86_64\n")
        for name in ("proton", "files/bin/wine", "files/bin/wineserver"):
            (proton / name).write_text(name)
        outside = root / "outside"
        outside.mkdir()
        (outside / "keep.txt").write_text("not ours")
        runner_temp = root / "runner-temp"
        runner_temp.mkdir()
        workspaces = []

        def fake_metadata(url, destination):
            if destination.name == "dotnet-releases.json":
                sdk = {"version": "8.0.100", "runtime-version": "8.0.0",
                       "files": [{"rid": "win-x64", "name": "dotnet-sdk-win-x64.zip", "url": "https://example.invalid/dotnet.zip", "hash": "0" * 128}]}
                return {"latest-sdk": "8.0.100", "releases": [{"sdk": sdk}]}
            workspaces.append(destination.parent)
            self._prefix(destination.parent, outside)
            if fail:
                raise RuntimeError("release metadata unavailable")
            name = "Godot_v4.7.2-stable_mono_win64.zip"
            return {"assets": [{"name": name, "digest": "sha256:" + "0" * 64,
                                "browser_download_url": f"https://github.com/godotengine/godot/releases/download/4.7.2-stable/{name}"}]}

        def fake_install(url, expected, algorithm, destination, archive):
            destination.mkdir(parents=True)
            if destination.name == "godot":
                (destination / "Godot_v4.7.2-stable_mono_win64.exe").write_bytes(b"fixture")
            return {"url": url, algorithm: expected}

        def fake_run(command, log, cwd, env, seconds, check=True):
            log.write_text("")
            if command[0] == "bash":
                logs = Path(env["DIRGE_SMOKE_LOG_DIR"])
                for filename, marker in (("demo.log", "DIRGE_SMOKE PASS"), ("sandbox.log", "SANDBOX_SMOKE PASS")):
                    (logs / filename).write_text(marker + "\n")
            else:
                log.write_text("NATIVE_TEST PASS\n")
            return {"exit_code": 0, "timed_out": False}

        environment = {"RUNNER_TEMP": str(runner_temp), "DIRGE_PROTON_LOG_DIR": str(root / "evidence"),
                       "PROTON_HOME": str(proton), "GODOT_VERSION": "4.7.2"}
        removal = {"side_effect": removal_error} if removal_error else {"wraps": windows_proton.remove_workspace}
        with patch.dict(windows_proton.os.environ, environment), \
             patch.object(windows_proton, "ROOT", repository), \
             patch.object(windows_proton, "metadata", side_effect=fake_metadata), \
             patch.object(windows_proton, "install_archive", side_effect=fake_install), \
             patch.object(windows_proton, "run", side_effect=fake_run), \
             patch.object(windows_proton, "quiesce_prefix"), \
             patch.object(windows_proton, "remove_workspace", **removal), \
             patch("sys.stdout", new_callable=io.StringIO), patch("sys.stderr", new_callable=io.StringIO):
            try:
                windows_proton.qualify()
                outcome = None
            except Exception as error:
                outcome = error
        self.assertEqual((outside / "keep.txt").read_text(), "not ours")
        self.assertEqual(len(workspaces), 1)
        work = workspaces[0]
        self.assertEqual(work.parent, runner_temp.resolve())
        receipt = repository / "addons/dirge/bin/windows-x64/proton-qualification.json"
        return outcome, work, root / "evidence" / work.name, receipt

    def test_qualification_removes_its_workspace_after_success(self):
        with tempfile.TemporaryDirectory() as directory:
            outcome, work, evidence, receipt = self._qualify(Path(directory))
            self.assertIsNone(outcome)
            self.assertFalse(os.path.lexists(work), "the Proton prefix must not survive in RUNNER_TEMP")
            self.assertEqual(json.loads(receipt.read_text())["status"], "PASS")
            self.assertEqual(json.loads((evidence / "qualification.json").read_text())["status"], "PASS")
            self.assertTrue((evidence / "template_release/smoke.log").is_file(), "logs are copied out before removal")

    def test_qualification_removes_its_workspace_after_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            outcome, work, evidence, receipt = self._qualify(Path(directory), fail=True)
            self.assertIsInstance(outcome, RuntimeError)
            self.assertIn("release metadata unavailable", str(outcome))
            self.assertFalse(os.path.lexists(work), "the Proton prefix must not survive in RUNNER_TEMP")
            self.assertFalse(receipt.exists())
            self.assertEqual(json.loads((evidence / "qualification.json").read_text())["status"], "FAIL")

    def test_workspace_removal_failure_never_masks_failure_or_qualifies_success(self):
        with tempfile.TemporaryDirectory() as directory:
            outcome, _work, _evidence, receipt = self._qualify(Path(directory) / "failed", fail=True,
                                                               removal_error=OSError("busy"))
            self.assertIsInstance(outcome, RuntimeError)
            self.assertIn("release metadata unavailable", str(outcome))
            outcome, _work, _evidence, receipt = self._qualify(Path(directory) / "passed", removal_error=OSError("busy"))
            self.assertIsInstance(outcome, OSError)
            self.assertFalse(receipt.exists(), "a run that leaks its workspace must not leave a PASS receipt")

if __name__ == "__main__":
    unittest.main()
