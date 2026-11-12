import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


class SmokeBuildTests(unittest.TestCase):
    def test_all_smoke_builds_are_isolated_and_preserve_build_failure(self):
        for compatibility in ("0", "1"):
            with self.subTest(compatibility=compatibility), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                (root / "scripts").mkdir()
                shutil.copy2(ROOT / "scripts/smoke.sh", root / "scripts/smoke.sh")
                (root / "addons/necronomidom").mkdir(parents=True)
                for asset in ("demo/audio/wind.wav", "demo/audio/click.wav",
                              "demo/sandbox/audio/calm_pad.wav", "demo/sandbox/audio/line_de.wav"):
                    path = root / asset
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.touch()
                tools = root / "tools"
                tools.mkdir()
                dotnet = tools / "dotnet"
                dotnet.write_text("#!/usr/bin/env python3\nimport json, os, sys\n"
                                  "from pathlib import Path\n"
                                  "Path(os.environ['BUILD_ARGUMENTS']).write_text(json.dumps(sys.argv[1:]))\n"
                                  "sys.exit(23)\n")
                dotnet.chmod(0o755)
                arguments = root / "arguments.json"
                logs = root / "logs"
                environment = dict(os.environ, PATH=str(tools) + os.pathsep + os.environ["PATH"],
                                   GODOT=shutil.which("true"), BUILD_ARGUMENTS=str(arguments),
                                   DIRGE_SMOKE_LOG_DIR=str(logs),
                                   DIRGE_SMOKE_ISOLATED_BUILD=compatibility)
                result = subprocess.run(["bash", "scripts/smoke.sh"], cwd=root,
                                        env=environment, capture_output=True, text=True)
                self.assertEqual(result.returncode, 23, result.stdout + result.stderr)
                args = json.loads(arguments.read_text())
                for flag in ("--disable-build-servers", "-m:1", "-nr:false", "-p:UseSharedCompilation=false"):
                    self.assertIn(flag, args)
                self.assertIn("-bl:" + str(logs / "managed-build.binlog"), args)
                self.assertTrue((logs / "managed-build.log").is_file())
                self.assertFalse((logs / "import.log").exists(), "A failed managed build must not run Godot.")


if __name__ == "__main__":
    unittest.main()
