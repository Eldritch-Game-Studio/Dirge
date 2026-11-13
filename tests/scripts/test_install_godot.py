"""Exercise installer commands against real standard-distribution-shaped ZIP files."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
import zipfile

SCRIPT = Path(__file__).resolve().parents[2] / "scripts/install-godot.sh"


class GodotInstallerTests(unittest.TestCase):
    def test_standard_linux_and_macos_archives_install_without_managed_payload(self):
        for system, member, asset in (
            ("Linux", "Godot_v4.7.2-stable_linux.x86_64", "Godot_v4.7.2-stable_linux.x86_64.zip"),
            ("Darwin", "Godot.app/Contents/MacOS/Godot", "Godot_v4.7.2-stable_macos.universal.zip"),
        ):
            with self.subTest(system=system), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                archive = root / "fixture.zip"
                with zipfile.ZipFile(archive, "w") as bundle:
                    bundle.writestr(member, "#!/bin/sh\nprintf '4.7.2.stable.fixture\\n'\n")
                commands = root / "commands"
                commands.mkdir()
                for name, body in {
                    "uname": '#!/bin/sh\nif [ "$1" = -m ]; then echo x86_64; else echo "$FIXTURE_SYSTEM"; fi\n',
                    "curl": '#!/usr/bin/env bash\nprintf "%s\\n" "$@" > "$REQUEST_LOG"\ncp "$FIXTURE_ARCHIVE" "${@: -1}"\n',
                }.items():
                    path = commands / name
                    path.write_text(body)
                    path.chmod(0o755)
                environment_file = root / "environment"
                request_log = root / "request"
                env = dict(os.environ, PATH=str(commands) + os.pathsep + os.environ["PATH"],
                           GODOT_VERSION="4.7.2", RUNNER_TEMP=str(root), GITHUB_ENV=str(environment_file),
                           FIXTURE_SYSTEM=system, FIXTURE_ARCHIVE=str(archive), REQUEST_LOG=str(request_log))
                result = subprocess.run(["bash", str(SCRIPT)], env=env, text=True, capture_output=True)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn("https://github.com/godotengine/godot/releases/download/4.7.2-stable/" + asset, request_log.read_text().splitlines())
                executable = Path(environment_file.read_text().strip().removeprefix("GODOT="))
                self.assertTrue(executable.is_file())
                version = subprocess.check_output([str(executable), "--version"], text=True)
                self.assertEqual(version.strip(), "4.7.2.stable.fixture")
                self.assertNotIn("mono", str(executable))


if __name__ == "__main__":
    unittest.main()
