import importlib.util
from pathlib import Path
import hashlib
import json
import struct
import tempfile
import unittest

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


if __name__ == "__main__":
    unittest.main()
