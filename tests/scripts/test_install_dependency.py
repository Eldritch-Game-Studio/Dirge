"""Real ZIP/command regressions; fixture libraries are not runtime qualification."""
import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
import unittest
import zipfile

SCRIPT = Path(__file__).resolve().parents[2] / "scripts/install-dependency.py"


class DependencyInstallerTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.environment = {key: value for key, value in os.environ.items() if not key.startswith("NECRONOMIDOM_")}
        self.destination = self.root / "addons/necronomidom"
        self.destination.mkdir(parents=True)
        (self.destination / "previous").write_bytes(b"existing dependency")

    def archive(self, extra=None, *, checksum=None, symlink=False):
        archive = self.root / "dependency.zip"
        entries = {
            "addons/necronomidom/necronomidom.gdextension": b'[configuration]\nentry_symbol="necronomidom_init"\n',
            "addons/necronomidom/bin/linux-x64/libnecronomidom.so": b"native test fixture, not executable",
            "addons/necronomidom/LICENSE": b"dependency license",
            "addons/necronomidom/ui/default.css": b"html { color: white; }",
        }
        entries.update(extra or {})
        with zipfile.ZipFile(archive, "w") as bundle:
            for name, data in entries.items():
                bundle.writestr(name, data)
            if symlink:
                info = zipfile.ZipInfo("addons/necronomidom/escape")
                info.create_system = 3
                info.external_attr = (stat.S_IFLNK | 0o777) << 16
                bundle.writestr(info, "../../outside")
        pin = {"url": archive.as_uri(), "sha256": checksum or hashlib.sha256(archive.read_bytes()).hexdigest(), "revision": "a" * 40}
        (self.root / "dependency-lock.json").write_text(json.dumps({"necronomidom": pin}))
        return archive, pin, entries

    def command(self, *arguments):
        return subprocess.run([sys.executable, str(SCRIPT), "--root", str(self.root), *arguments], env=self.environment, text=True, capture_output=True)

    def assert_preserved(self):
        self.assertEqual((self.destination / "previous").read_bytes(), b"existing dependency")
        self.assertFalse(list((self.root / "addons").glob(".necronomidom-*")))

    def test_installs_exact_file_archive_and_preserves_verified_zip(self):
        archive, pin, entries = self.archive()
        output = self.root / "evidence/dependency.zip"
        result = self.command("--archive-output", str(output))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(output.read_bytes(), archive.read_bytes())
        self.assertFalse((self.destination / "previous").exists())
        receipt = json.loads((self.destination / ".dirge-dependency.json").read_text())
        for key in pin:
            self.assertEqual(receipt[key], pin[key])
        for name, data in entries.items():
            relative = name.removeprefix("addons/necronomidom/")
            self.assertEqual((self.destination / relative).read_bytes(), data)
            self.assertEqual(receipt["files"][relative], hashlib.sha256(data).hexdigest())

    def test_checksum_failure_does_not_replace_existing_install(self):
        self.archive(checksum="0" * 64)
        result = self.command()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("checksum mismatch", result.stderr)
        self.assert_preserved()

    def test_no_implicit_old_release_fallback(self):
        result = self.command()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("no released-version fallback", result.stderr)
        self.assert_preserved()

    def test_rejects_managed_packages_and_escaping_paths_before_replacement(self):
        for name in ("addons/necronomidom/source.cs", "addons/necronomidom/build.targets", "addons/necronomidom/host.exe", "addons/necronomidom/../outside", "../escape", "/absolute", "addons/necronomidom\\escape"):
            with self.subTest(name=name):
                self.archive({name: b"invalid"})
                result = self.command()
                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.assert_preserved()
        self.archive(symlink=True)
        self.assertNotEqual(self.command().returncode, 0)
        self.assert_preserved()

    def test_explicit_pin_overrides_lock_only_when_complete(self):
        _, pin, _ = self.archive()
        (self.root / "dependency-lock.json").unlink()
        self.environment["NECRONOMIDOM_PACKAGE_URL"] = pin["url"]
        self.assertNotEqual(self.command().returncode, 0)
        self.assert_preserved()
        self.environment.update(NECRONOMIDOM_PACKAGE_SHA256=pin["sha256"], NECRONOMIDOM_REVISION=pin["revision"])
        result = self.command()
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_rejects_source_only_zip(self):
        archive, _, _ = self.archive()
        with zipfile.ZipFile(archive, "w") as bundle:
            bundle.writestr("addons/necronomidom/LICENSE", "license")
        lock = json.loads((self.root / "dependency-lock.json").read_text())
        lock["necronomidom"]["sha256"] = hashlib.sha256(archive.read_bytes()).hexdigest()
        (self.root / "dependency-lock.json").write_text(json.dumps(lock))
        result = self.command()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("lacks the NecronomiDOM native runtime", result.stderr)
        self.assert_preserved()


if __name__ == "__main__":
    unittest.main()
