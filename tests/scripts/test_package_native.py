import importlib.util
from pathlib import Path
import hashlib
import json
import struct
import shutil
import subprocess
import tempfile
import unittest
import zipfile

SCRIPT = Path(__file__).resolve().parents[2] / "scripts/package-native.py"
spec = importlib.util.spec_from_file_location("package_native", SCRIPT)
package_native = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package_native)


def universal():
    # Minimal headers for testing architecture/bounds validation, not loadable libraries.
    data = bytearray(struct.pack(">II", 0xCAFEBABE, 2))
    data.extend(struct.pack(">IIIII", 0x01000007, 3, 48, 32, 0))
    data.extend(struct.pack(">IIIII", 0x0100000C, 0, 80, 32, 0))
    for cpu in (0x01000007, 0x0100000C):
        data.extend(struct.pack("<II", 0xFEEDFACF, cpu) + bytes(24))
    return data


def qualification(root):
    # Unit-test metadata only; not evidence from an actual compatibility run.
    targets = {}
    for target in ("template_debug", "template_release"):
        path = root / "windows-x64" / f"libdirge_native.windows.{target}.x86_64.dll"
        targets[target] = {
            "status": "PASS", "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "assertions": ["DIRGE_SMOKE PASS", "SANDBOX_SMOKE PASS", "NATIVE_TEST PASS"],
            "smoke": {"exit_code": 0, "timed_out": False},
            "native": {"exit_code": 0, "timed_out": False},
        }
    receipt = root / "windows-x64/proton-qualification.json"
    receipt.write_text(json.dumps({"schema": 1, "status": "PASS",
        "coverage": "windows-x64-proton-compatibility", "source_revision": "a" * 40,
        "targets": targets}))
    return receipt


class NativePackageTests(unittest.TestCase):
    def test_accepts_both_architectures(self):
        self.assertEqual(package_native.universal_architectures(universal()), {0x01000007, 0x0100000C})

    def test_rejects_thin_library(self):
        with self.assertRaisesRegex(ValueError, "universal"):
            package_native.universal_architectures(universal()[48:80])

    def test_rejects_truncated_slice(self):
        with self.assertRaisesRegex(ValueError, "bounds"):
            package_native.universal_architectures(universal()[:-1])

    def test_rejects_mislabeled_slice(self):
        data = universal()
        struct.pack_into("<I", data, 84, 0x01000007)
        with self.assertRaisesRegex(ValueError, "does not match"):
            package_native.universal_architectures(data)

    def test_rejects_duplicate_architecture(self):
        data = universal()
        struct.pack_into(">I", data, 28, 0x01000007)
        struct.pack_into("<I", data, 84, 0x01000007)
        with self.assertRaisesRegex(ValueError, "missing arm64"):
            package_native.universal_architectures(data)

    def test_requires_every_registered_configuration(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            files = []
            for target in ("template_debug", "template_release"):
                for platform, extension in (("linux", "so"), ("windows", "dll")):
                    path = root / f"{platform}-x64" / f"libdirge_native.{platform}.{target}.x86_64.{extension}"
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_bytes(b"test fixture")
                    files.append(path)
                name = f"libdirge_native.macos.{target}"
                path = root / "macos" / f"{name}.framework" / name
                path.parent.mkdir(parents=True)
                path.write_bytes(universal())
                files.append(path)
            receipt = qualification(root)
            package_native.validate(root)
            for path in files:
                with self.subTest(path=path.name):
                    contents = path.read_bytes()
                    path.unlink()
                    with self.assertRaisesRegex(ValueError, "missing native library"):
                        package_native.validate(root)
                    path.write_bytes(contents)
            receipt.unlink()
            with self.assertRaisesRegex(ValueError, "missing or invalid Windows Proton"):
                package_native.validate(root)
            qualification(root)
            with self.assertRaisesRegex(ValueError, "source revision"):
                package_native.validate(root, "b" * 40)
            package_native.validate(root, "a" * 40)
            windows = next(path for path in files if path.suffix == ".dll")
            windows.write_bytes(b"unqualified replacement")
            with self.assertRaisesRegex(ValueError, "matching successful Proton"):
                package_native.validate(root)

    def test_rejects_failed_or_timed_out_proton_runs(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for target in ("template_debug", "template_release"):
                path = root / "windows-x64" / f"libdirge_native.windows.{target}.x86_64.dll"
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"unit-test fixture")
            for step in ("smoke", "native"):
                for field, value in (("exit_code", 1), ("timed_out", True)):
                    receipt = qualification(root)
                    evidence = json.loads(receipt.read_text())
                    evidence["targets"]["template_release"][step][field] = value
                    receipt.write_text(json.dumps(evidence))
                    with self.assertRaisesRegex(ValueError, "matching successful Proton"):
                        package_native.validate_windows_qualification(root)


class ArchivePackageTests(unittest.TestCase):
    """Synthetic binaries exercise the real ZIP builder, never runtime qualification."""

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        repository = SCRIPT.parents[1]
        for relative in ("scripts/package.sh", "scripts/package-native.py",
                         "addons/dirge/plugin.cfg", "addons/dirge/dirge_native.gdextension"):
            destination = self.root / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(repository / relative, destination)
        self.documents = ("LICENSE", "README.md", "CHANGELOG.md", "THIRD_PARTY_NOTICES.md")
        for document in self.documents:
            (self.root / document).write_text(f"repository {document}\n")
            (self.root / "addons/dirge" / document).write_text("short addon copy\n")
        self.excluded = ("model/bin/generated.dll", "model/obj/generated.cs",
                         "runtime/obj/generated.cs", ".DS_Store", "sample.import.tmp",
                         ".git/config", ".github/workflows/ci.yml", ".godot/editor/cache",
                         ".gitignore", ".gitattributes", "Thumbs.db", "__MACOSX/._resource",
                         "__pycache__/generated.pyc")
        for relative in (*self.excluded, "runtime/source.cs", "runtime/source.cs.uid"):
            path = self.root / "addons/dirge" / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("fixture\n")
        self.libraries = []
        native = self.root / "addons/dirge/bin"
        for target in ("template_debug", "template_release"):
            for platform, extension in (("linux", "so"), ("windows", "dll")):
                path = native / f"{platform}-x64/libdirge_native.{platform}.{target}.x86_64.{extension}"
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"synthetic native test fixture")
                self.libraries.append(path.relative_to(self.root).as_posix())
            name = f"libdirge_native.macos.{target}"
            path = native / "macos" / f"{name}.framework" / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(universal())
            self.libraries.append(path.relative_to(self.root).as_posix())
        self.run_command("git", "init", "-q")
        self.run_command("git", "-c", "user.name=Package test", "-c",
                         "user.email=package-test@example.invalid", "commit",
                         "--allow-empty", "-qm", "Synthetic package fixture")
        self.revision = self.run_command("git", "rev-parse", "HEAD").stdout.strip()
        receipt = qualification(native)
        evidence = json.loads(receipt.read_text())
        evidence["source_revision"] = self.revision
        receipt.write_text(json.dumps(evidence))
        self.run_command("bash", "scripts/package.sh")
        self.archive = next((self.root / "dist").glob("Dirge-v*.zip"))

    def run_command(self, *arguments, check=True):
        result = subprocess.run(arguments, cwd=self.root, text=True, capture_output=True)
        if check:
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        return result

    def rewrite_archive(self, *, omit=(), replace=None, extra=None):
        output = self.root / "changed.zip"
        with zipfile.ZipFile(self.archive) as original, zipfile.ZipFile(output, "w") as changed:
            for member in original.namelist():
                if member not in omit:
                    changed.writestr(member, (replace or {}).get(member, original.read(member)))
            for member, data in (extra or {}).items():
                changed.writestr(member, data)
        return output

    def test_real_package_has_all_mappings_and_namespaced_documents(self):
        package_native.validate_archive(self.archive, self.revision)
        with zipfile.ZipFile(self.archive) as package:
            names = package.namelist()
            self.assertEqual(len(names), len(set(names)))
            self.assertTrue(all(name.startswith("addons/dirge/") for name in names))
            for document in self.documents:
                self.assertEqual(package.read(f"addons/dirge/{document}"),
                                 (self.root / document).read_bytes())
            for library in self.libraries:
                self.assertIn(library, names)
            for relative in self.excluded:
                self.assertNotIn(f"addons/dirge/{relative}", names)
            self.assertIn("addons/dirge/runtime/source.cs.uid", names)

    def test_archive_rejects_each_missing_or_empty_library(self):
        for library in self.libraries:
            for empty in (False, True):
                with self.subTest(library=library, empty=empty):
                    archive = self.rewrite_archive(
                        replace={library: b""} if empty else None,
                        omit=() if empty else (library,))
                    with self.assertRaisesRegex(ValueError, "missing native library"):
                        package_native.validate_archive(archive, self.revision)

    def test_builder_rejects_missing_inputs_without_replacing_published_zip(self):
        published = self.archive.read_bytes()
        for library in self.libraries:
            with self.subTest(library=library):
                path = self.root / library
                data = path.read_bytes()
                path.unlink()
                result = self.run_command("bash", "scripts/package.sh", check=False)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("missing native library", result.stderr)
                self.assertEqual(self.archive.read_bytes(), published)
                path.write_bytes(data)
        self.assertFalse(list((self.root / "dist").glob(".package.*")))

    def test_archive_checks_manifest_not_only_known_filenames(self):
        manifest = "addons/dirge/dirge_native.gdextension"
        data = (self.root / manifest).read_bytes()
        data += b'\nlinux.debug.arm64 = "res://addons/dirge/bin/linux-arm64/new.so"\n'
        archive = self.rewrite_archive(replace={manifest: data})
        with self.assertRaisesRegex(ValueError, "linux.debug.arm64"):
            package_native.validate_archive(archive, self.revision)

    def test_archive_rejects_root_documents_and_missing_namespaced_documents(self):
        for document in self.documents:
            with self.subTest(document=document):
                archive = self.rewrite_archive(extra={document: b"ambiguous"})
                with self.assertRaisesRegex(ValueError, "inside addons/dirge"):
                    package_native.validate_archive(archive, self.revision)
                archive = self.rewrite_archive(omit=(f"addons/dirge/{document}",))
                with self.assertRaisesRegex(ValueError, "missing package document"):
                    package_native.validate_archive(archive, self.revision)

    def test_archive_rejects_store_prohibited_payloads_and_junk(self):
        for name in ("tool.exe", "library.nupkg", "symbols.snupkg", "tool.EXE",
                     ".gitignore", "Thumbs.db", "__MACOSX/._resource", ".github/workflows/ci.yml"):
            with self.subTest(name=name):
                archive = self.rewrite_archive(extra={f"addons/dirge/{name}": b"unwanted"})
                with self.assertRaisesRegex(ValueError, "not an addon asset|unnecessary package member"):
                    package_native.validate_archive(archive, self.revision)

    def test_archive_keeps_architecture_and_qualification_checks(self):
        macos = next(name for name in self.libraries if ".macos.template_debug." in name)
        archive = self.rewrite_archive(replace={macos: universal()[48:80]})
        with self.assertRaisesRegex(ValueError, "universal"):
            package_native.validate_archive(archive, self.revision)
        windows = next(name for name in self.libraries if ".windows.template_debug." in name)
        archive = self.rewrite_archive(replace={windows: b"different DLL"})
        with self.assertRaisesRegex(ValueError, "matching successful Proton"):
            package_native.validate_archive(archive, self.revision)
        with self.assertRaisesRegex(ValueError, "source revision"):
            package_native.validate_archive(self.archive, "b" * 40)
        archive = self.rewrite_archive(omit=("addons/dirge/bin/windows-x64/proton-qualification.json",))
        with self.assertRaisesRegex(ValueError, "missing or invalid Windows Proton"):
            package_native.validate_archive(archive, self.revision)

    def test_partial_diagnostic_is_explicit_and_still_namespaces_documents(self):
        shutil.rmtree(self.root / "addons/dirge/bin")
        self.run_command("bash", "scripts/package.sh", "--suffix", "-partial")
        archive = next((self.root / "dist").glob("*-partial.zip"))
        package_native.validate_archive(archive, self.revision, partial=True)
        with self.assertRaisesRegex(ValueError, "missing native library"):
            package_native.validate_archive(archive, self.revision)
        result = self.run_command("python3", "scripts/package-native.py",
                                  str(self.archive), "--partial", check=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("requires a -partial.zip", result.stderr)


if __name__ == "__main__":
    unittest.main()
