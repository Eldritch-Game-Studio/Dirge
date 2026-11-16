"""Real ZIP/command regressions for the hash-locked NecronomiDOM managed-host dependency."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts/install-dependency.py"
REVISION = "b" * 40


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def managed_zip(path, files, *, revision=REVISION, manifest=None, extra=None, symlink=False):
    """A package-managed.py-shaped archive: addons/necronomidom/** plus addon-package.json."""
    metadata = {
        "schema": "eldritch.managed-addon/1",
        "addon": "necronomidom",
        "repository": "https://github.com/Eldritch-Game-Studio/NecronomiDOM",
        "revision": revision,
        "profile": "managed-host",
        "files": manifest if manifest is not None else {name: hashlib.sha256(data).hexdigest() for name, data in files.items()},
    }
    with zipfile.ZipFile(path, "w") as bundle:
        for name, data in files.items():
            bundle.writestr("addons/necronomidom/" + name, data)
        bundle.writestr("addon-package.json", json.dumps(metadata))
        for name, data in (extra or {}).items():
            bundle.writestr(name, data)
        if symlink:
            info = zipfile.ZipInfo("addons/necronomidom/escape")
            info.create_system = 3
            info.external_attr = (stat.S_IFLNK | 0o777) << 16
            bundle.writestr(info, "../../outside")
    return hashlib.sha256(path.read_bytes()).hexdigest()


FILES = {
    "NecronomiDom.targets": b"<Project />",
    "core/NecronomiDom.csproj": b"<Project Sdk=\"Microsoft.NET.Sdk\" />",
    "runtime/DocumentHost.cs": b"// host",
}


class DependencyInstallerTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.environment = {key: value for key, value in os.environ.items() if not key.startswith("NECRONOMIDOM_")}
        self.destination = self.root / "addons/necronomidom"
        self.destination.mkdir(parents=True)
        (self.destination / "previous").write_bytes(b"existing dependency")

    def candidate(self, archive, sha256, revision=REVISION):
        self.environment.update(NECRONOMIDOM_PACKAGE_URL=archive.as_uri(), NECRONOMIDOM_PACKAGE_SHA256=sha256,
                                NECRONOMIDOM_REVISION=revision)

    def command(self):
        return subprocess.run([sys.executable, str(SCRIPT), "--root", str(self.root)], env=self.environment, text=True,
                              capture_output=True)

    def assert_preserved(self, result, message):
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(message, result.stderr)
        self.assertEqual((self.destination / "previous").read_bytes(), b"existing dependency")
        self.assertFalse(list((self.root / "addons").glob(".necronomidom-*")))

    def test_installs_verified_candidate_then_reuses_it_and_repairs_tampering(self):
        archive = self.root / "nd.zip"
        self.candidate(archive, managed_zip(archive, FILES))
        result = self.command()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse((self.destination / "previous").exists())
        for name, data in FILES.items():
            self.assertEqual((self.destination / name).read_bytes(), data)
        self.assertFalse((self.destination.parent / "addon-package.json").exists())
        marker = json.loads((self.destination / ".dirge-dependency.json").read_text())
        self.assertEqual(marker["revision"], REVISION)
        self.assertIn("already installed", self.command().stdout)
        (self.destination / "runtime/DocumentHost.cs").write_bytes(b"// edited")
        result = self.command()
        self.assertIn("installed NecronomiDOM", result.stdout)
        self.assertEqual((self.destination / "runtime/DocumentHost.cs").read_bytes(), FILES["runtime/DocumentHost.cs"])

    def test_installs_host_import_settings_and_refreshes_when_they_change(self):
        archive = self.root / "nd.zip"
        self.candidate(archive, managed_zip(archive, FILES))
        settings = self.root / "host-imports/necronomidom/core/NecronomiDom.csproj.import"
        settings.parent.mkdir(parents=True)
        settings.write_text("[remap]\nimporter=\"keep\"\n")
        self.assertEqual(self.command().returncode, 0)
        installed = self.destination / "core/NecronomiDom.csproj.import"
        self.assertEqual(installed.read_text(), settings.read_text())
        settings.write_text("[remap]\nimporter=\"changed\"\n")
        self.assertIn("installed NecronomiDOM", self.command().stdout)
        self.assertEqual(installed.read_text(), settings.read_text())
        orphan = self.root / "host-imports/necronomidom/fonts/missing.otf.import"
        orphan.parent.mkdir(parents=True)
        orphan.write_text("x")
        result = self.command()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("no matching asset", result.stderr)

    def test_rejects_checksum_mismatch(self):
        archive = self.root / "nd.zip"
        managed_zip(archive, FILES)
        self.candidate(archive, "0" * 64)
        self.assert_preserved(self.command(), "checksum mismatch")

    def test_rejects_package_of_another_revision(self):
        archive = self.root / "nd.zip"
        self.candidate(archive, managed_zip(archive, FILES, revision="c" * 40))
        self.assert_preserved(self.command(), "revision")

    def test_rejects_manifest_that_does_not_match_contents(self):
        archive = self.root / "nd.zip"
        manifest = {name: hashlib.sha256(data).hexdigest() for name, data in FILES.items()}
        manifest["runtime/DocumentHost.cs"] = "0" * 64
        self.candidate(archive, managed_zip(archive, FILES, manifest=manifest))
        self.assert_preserved(self.command(), "file manifest")

    def test_rejects_full_release_zip_without_managed_manifest(self):
        archive = self.root / "nd.zip"
        with zipfile.ZipFile(archive, "w") as bundle:
            bundle.writestr("addons/necronomidom/NecronomiDom.targets", "<Project />")
        self.candidate(archive, hashlib.sha256(archive.read_bytes()).hexdigest())
        self.assert_preserved(self.command(), "only managed-host packages")

    def test_rejects_members_outside_the_addon_and_symlinks(self):
        for kwargs, message in (({"extra": {"project.godot": b"x"}}, "outside addons/necronomidom/"),
                                ({"symlink": True}, "unsafe")):
            with self.subTest(message=message):
                archive = self.root / "nd.zip"
                self.candidate(archive, managed_zip(archive, FILES, **kwargs))
                self.assert_preserved(self.command(), message)

    def test_requires_all_candidate_variables_together(self):
        self.environment["NECRONOMIDOM_PACKAGE_URL"] = (self.root / "nd.zip").as_uri()
        self.assert_preserved(self.command(), "together")

    def test_lock_is_required_and_strict(self):
        self.assert_preserved(self.command(), "dependency-lock.json is required")
        lock = {"repository": "Eldritch-Game-Studio/NecronomiDOM", "sha": REVISION, "release_tag": "managed-" + REVISION[:12],
                "zip": f"necronomidom-managed-{REVISION}.zip", "zip_sha256": "a" * 64}
        for key, value, message in (("release_tag", "v1.1.5", "managed-<sha12>"),
                                    ("repository", "someone/NecronomiDOM", "must come from"),
                                    ("zip", "NecronomiDOM-v1.1.5.zip", "package-managed.py name"),
                                    ("zip_sha256", "abc", "64 lowercase hex")):
            with self.subTest(key=key):
                (self.root / "dependency-lock.json").write_text(json.dumps({"necronomidom": dict(lock, **{key: value})}))
                self.assert_preserved(self.command(), message)

    def test_repository_lock_pins_a_ci_release_of_the_managed_host_package(self):
        installer = load("install_dependency", SCRIPT)
        pin = installer.read_lock(ROOT)
        self.assertEqual(installer.release_url(pin, pin["zip"]),
                         f"https://github.com/Eldritch-Game-Studio/NecronomiDOM/releases/download/managed-{pin['sha'][:12]}/"
                         f"necronomidom-managed-{pin['sha']}.zip")


class LockUpdateTests(unittest.TestCase):
    """The receiver's lock transition with GitHub and release downloads replaced by recorded responses."""

    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.updater = load("update_dependency_lock", ROOT / "scripts/update-dependency-lock.py")
        self.old = "a" * 40
        self.new = REVISION
        self.archive = self.root / "nd.zip"
        self.digest = managed_zip(self.archive, FILES)
        self.lock = {"repository": "Eldritch-Game-Studio/NecronomiDOM", "sha": self.old, "release_tag": "managed-" + self.old[:12],
                     "zip": f"necronomidom-managed-{self.old}.zip", "zip_sha256": "d" * 64}
        (self.root / "dependency-lock.json").write_text(json.dumps({"necronomidom": self.lock}))
        self.tag = "managed-" + self.new[:12]
        self.zip = f"necronomidom-managed-{self.new}.zip"
        self.provenance = {"schema": "eldritch.managed-provenance/1", "package": "necronomidom",
                           "repository": "Eldritch-Game-Studio/NecronomiDOM", "sha": self.new, "branch": "main",
                           "ref": "refs/heads/main", "run_id": 41, "run_attempt": 1, "release_tag": self.tag,
                           "zip": self.zip, "zip_sha256": self.digest, "upstream": {}}
        self.compare = {f"{self.old}...{self.new}": "ahead", f"{self.new}...main": "identical"}
        self.run = {"id": 41, "head_sha": self.new, "conclusion": None,
                    "repository": {"full_name": "Eldritch-Game-Studio/NecronomiDOM"}}
        self.release = {"digest": "sha256:" + self.digest}

    def api(self, path):
        repository = "repos/Eldritch-Game-Studio/NecronomiDOM/"
        self.assertTrue(path.startswith(repository), path)
        path = path[len(repository):]
        if path.startswith("compare/"):
            return {"status": self.compare[path[len("compare/"):]]}
        if path.startswith("actions/runs/"):
            return dict(self.run, id=int(path.rsplit("/", 1)[1]))
        if path == "releases/tags/" + self.tag:
            return {"assets": [{"name": self.zip, **self.release}, {"name": "provenance.json"}]}
        raise AssertionError(path)

    def fetch(self, url):
        base = f"https://github.com/Eldritch-Game-Studio/NecronomiDOM/releases/download/{self.tag}/"
        if url == base + "provenance.json":
            return json.dumps(self.provenance).encode()
        if url == base + self.zip:
            return self.archive.read_bytes()
        raise AssertionError(url)

    def update(self, sha=None, digest=None):
        sha = sha or self.new
        return self.updater.update(self.root, "necronomidom", sha, "managed-" + sha[:12], digest or self.digest,
                                   api=self.api, fetch=self.fetch)

    def locked(self):
        return json.loads((self.root / "dependency-lock.json").read_text())["necronomidom"]

    def test_newer_ci_package_advances_the_lock(self):
        result = self.update()
        self.assertTrue(result["changed"])
        self.assertEqual(self.locked(), {"repository": "Eldritch-Game-Studio/NecronomiDOM", "sha": self.new,
                                         "release_tag": self.tag, "zip": self.zip, "zip_sha256": self.digest})

    def test_older_or_same_package_never_moves_the_lock(self):
        self.compare[f"{self.old}...{self.new}"] = "behind"
        self.assertEqual(self.update()["relation"], "older")
        self.assertEqual(self.locked(), self.lock)
        self.assertEqual(self.update(self.old, "d" * 64)["relation"], "same")
        with self.assertRaisesRegex(ValueError, "different package identity"):
            self.update(self.old, "e" * 64)

    def test_backfill_requires_the_successful_qualifying_run(self):
        self.provenance["qualified_run_id"] = 40
        self.run["conclusion"] = "success"
        self.assertTrue(self.update()["changed"])
        (self.root / "dependency-lock.json").write_text(json.dumps({"necronomidom": self.lock}))
        self.run["conclusion"] = "failure"
        with self.assertRaisesRegex(ValueError, "qualifying CI run 40"):
            self.update()

    def test_rejections_leave_the_lock_unchanged(self):
        cases = (
            ("provenance", lambda: self.provenance.update(branch="feature"), "provenance branch"),
            ("payload checksum", lambda: self.provenance.update(zip_sha256="f" * 64), "provenance zip_sha256"),
            ("not on main", lambda: self.compare.update({f"{self.new}...main": "diverged"}), "not on"),
            ("foreign run", lambda: self.run.update(head_sha="9" * 40), "did not build"),
            ("failed run", lambda: self.run.update(conclusion="failure"), "concluded"),
            ("asset digest", lambda: self.release.update(digest="sha256:" + "0" * 64), "release asset digest"),
        )
        for name, mutate, message in cases:
            with self.subTest(name=name):
                saved = (dict(self.provenance), dict(self.compare), dict(self.run), dict(self.release))
                mutate()
                with self.assertRaisesRegex(ValueError, message):
                    self.update()
                self.assertEqual(self.locked(), self.lock)
                self.provenance, self.compare, self.run, self.release = map(dict, saved)
        with self.assertRaisesRegex(ValueError, "consumes only necronomidom"):
            self.updater.update(self.root, "dirge", self.new, self.tag, self.digest, api=self.api, fetch=self.fetch)


if __name__ == "__main__":
    unittest.main()
