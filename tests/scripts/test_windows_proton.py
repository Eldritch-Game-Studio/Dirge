import importlib.util
import hashlib
from pathlib import Path
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


if __name__ == "__main__":
    unittest.main()
