import importlib.util
import io
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
import shutil
from unittest.mock import patch
import zipfile

SCRIPT = Path(__file__).resolve().parents[2] / "scripts/windows-proton.py"
spec = importlib.util.spec_from_file_location("windows_proton", SCRIPT)
windows_proton = importlib.util.module_from_spec(spec)
spec.loader.exec_module(windows_proton)


class WindowsProtonTests(unittest.TestCase):
    def test_engine_errors_fail_even_alongside_pass_marker(self):
        with tempfile.TemporaryDirectory() as directory:
            log = Path(directory) / "engine.log"
            for error in ("ERROR: native library missing", "SCRIPT ERROR: invalid call"):
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

    def test_installs_verified_standard_windows_archive_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "standard.zip"
            executable = "Godot_v4.7.2-stable_win64_console.exe"
            with zipfile.ZipFile(source, "w") as archive:
                archive.writestr(executable, b"standard Windows fixture")
            checksum = hashlib.sha256(source.read_bytes()).hexdigest()
            with patch.object(windows_proton, "fetch", side_effect=lambda url, destination: shutil.copyfile(source, destination)):
                receipt = windows_proton.install_archive(
                    "https://example.invalid/Godot_v4.7.2-stable_win64.exe.zip",
                    checksum, "sha256", root / "godot", root / "download.zip")
                self.assertEqual((root / "godot" / executable).read_bytes(), b"standard Windows fixture")
                self.assertEqual(receipt["sha256"], checksum)
                with self.assertRaisesRegex(ValueError, "digest mismatch"):
                    windows_proton.install_archive("https://example.invalid/standard.zip",
                        "0" * 64, "sha256", root / "rejected", root / "download.zip")
                self.assertFalse((root / "rejected").exists())

    def test_version_probe_reports_redirected_engine_output_only(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "logs").mkdir()
            commands = []

            def fake_run(command, log, cwd, env, seconds, check=True):
                commands.append(command)
                log.write_text("Proton: Upgrading prefix\n")
                script = Path(log).with_suffix(".version.cmd")
                self.assertIn(" --version > ", script.read_text())
                Path(log).with_suffix(".version.txt").write_text("4.7.2.stable.official.ed1daf0bf\r\n")
                return {"exit_code": 0, "timed_out": False}

            env = {"DIRGE_PROTON_WORK": str(root), "DIRGE_PROTON_HOME": str(root),
                   "DIRGE_WINDOWS_GODOT": str(root / "Godot_v4.7.2-stable_win64_console.exe")}
            with patch.dict(windows_proton.os.environ, env), \
                 patch.object(windows_proton, "run", side_effect=fake_run), \
                 patch.object(windows_proton, "quiesce_prefix"), \
                 patch("sys.stdout", new_callable=io.StringIO) as stdout, \
                 patch("sys.stderr", new_callable=io.StringIO) as stderr:
                windows_proton.godot_run(["--version"])
            self.assertEqual(stdout.getvalue(), "4.7.2.stable.official.ed1daf0bf\n")
            self.assertIn("Proton: Upgrading prefix", stderr.getvalue())
            self.assertEqual(commands[0][-3:-1], ["C:\\windows\\system32\\cmd.exe", "/c"])

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
        (repository / "addons/dirge/dirge_native.gdextension").write_text('windows.debug.x86_64 = "windows.template_debug.x86_64.dll"\n')
        for target in windows_proton.TARGETS:
            (repository / f"addons/dirge/bin/windows-x64/libdirge_native.windows.{target}.x86_64.dll").write_bytes(target.encode())
        git = ["git", "-c", "user.name=fixture", "-c", "user.email=fixture@example.invalid"]
        subprocess.run(["git", "init", "-q", str(repository)], check=True)
        subprocess.run([*git, "add", "project.godot", "addons/dirge/dirge_native.gdextension"], cwd=repository, check=True)
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
            workspaces.append(destination.parent)
            self._prefix(destination.parent, outside)
            if fail:
                raise RuntimeError("release metadata unavailable")
            name = "Godot_v4.7.2-stable_win64.exe.zip"
            return {"assets": [{"name": name, "digest": "sha256:" + "0" * 64,
                                "browser_download_url": f"https://github.com/godotengine/godot/releases/download/4.7.2-stable/{name}"}]}

        def fake_install(url, expected, algorithm, destination, archive):
            destination.mkdir(parents=True)
            (destination / "Godot_v4.7.2-stable_win64_console.exe").write_bytes(b"fixture")
            return {"url": url, algorithm: expected}

        def fake_run(command, log, cwd, env, seconds, check=True):
            log.write_text("")
            if command[0] == "bash":
                logs = Path(env["DIRGE_SMOKE_LOG_DIR"])
                for filename, marker in (("gdscript.log", "DIRGE_TEST PASS assertions=1 failures=0"),
                                         ("demo.log", "DIRGE_SMOKE PASS"), ("sandbox.log", "SANDBOX_SMOKE PASS")):
                    (logs / filename).write_text(marker + "\n")
            else:
                log.write_text("NATIVE_TEST PASS\n")
            return {"exit_code": 0, "timed_out": False}

        environment = {"RUNNER_TEMP": str(runner_temp), "DIRGE_PROTON_LOG_DIR": str(root / "evidence"),
                       "PROTON_HOME": str(proton), "GODOT_VERSION": "4.7.2"}
        removal = {"side_effect": removal_error} if removal_error else {"wraps": windows_proton.remove_workspace}
        # Portable script tests also run on the macOS runner; qualify() itself admits only Linux hosts.
        with patch.object(windows_proton.sys, "platform", "linux"), \
             patch.dict(windows_proton.os.environ, environment), \
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
        self.assertEqual(len(workspaces), 1, f"qualify() stopped before creating its workspace: {outcome!r}")
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
