import importlib.util
from pathlib import Path
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
                path = root / "macos" / f"{name}.framework" / f"{name}.dylib"
                path.parent.mkdir(parents=True)
                path.write_bytes(universal())
                files.append(path)
            package_native.validate(root)
            for path in files:
                with self.subTest(path=path.name):
                    contents = path.read_bytes()
                    path.unlink()
                    with self.assertRaisesRegex(ValueError, "missing native library"):
                        package_native.validate(root)
                    path.write_bytes(contents)


if __name__ == "__main__":
    unittest.main()
