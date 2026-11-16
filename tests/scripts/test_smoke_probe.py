import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
FAKE_GODOT = """#!/usr/bin/env python3
import os, sys
args = sys.argv[1:]
if args == ['--version']:
    print('4.7.2.stable.official.fake')
elif 'res://tests/gdscript/run.gd' in args:
    print(os.environ['SUITE_PROBE'] + 'DIRGE_TEST PASS assertions=1 failures=0')
elif 'res://demo/demo.tscn' in args:
    print('DIRGE_SMOKE PASS')
elif 'res://demo/sandbox/sandbox.tscn' in args:
    print('SANDBOX_SMOKE PASS')
elif 'res://native/tests/sendreturn.tscn' in args:
    print('NATIVE_TEST PASS')
"""


class SmokeProbeTests(unittest.TestCase):
    def test_native_pending_pause_probe_result_is_required_and_summarized(self):
        cases = {
            "lacks": ("DIRGE_NATIVE_PENDING_PAUSE engine lacks pending-pause persistence: 5 native-pause assertions skipped\n", 0, "::warning"),
            "persists": ("DIRGE_NATIVE_PENDING_PAUSE engine persists pending pause: 5 native-pause assertions enforced\n", 0, "::notice"),
            "missing": ("", 1, None),
        }
        for name, (probe, code, annotation) in cases.items():
            with self.subTest(name), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                (root / "scripts").mkdir()
                shutil.copy2(ROOT / "scripts/smoke.sh", root / "scripts/smoke.sh")
                fetch = root / "scripts/fetch-deps.sh"
                fetch.write_text("#!/usr/bin/env bash\n")
                fetch.chmod(0o755)
                (root / "project.godot").write_text("; fake\n")
                for asset in ("demo/audio/wind.wav", "demo/audio/click.wav",
                              "demo/sandbox/audio/calm_pad.wav", "demo/sandbox/audio/line_de.wav"):
                    path = root / asset
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.touch()
                godot = root / "godot"
                godot.write_text(FAKE_GODOT)
                godot.chmod(0o755)
                summary = root / "summary.md"
                environment = dict(os.environ, GODOT=str(godot), SUITE_PROBE=probe,
                                   DIRGE_SMOKE_LOG_DIR=str(root / "logs"), GITHUB_STEP_SUMMARY=str(summary),
                                   GITHUB_ACTIONS="true", DIRGE_PROTON_WORK=str(root / "proton"))
                environment.pop("GODOT_ARCH", None)
                result = subprocess.run(["bash", "scripts/smoke.sh"], cwd=root,
                                        env=environment, capture_output=True, text=True)
                self.assertEqual(result.returncode, code, result.stdout + result.stderr)
                if annotation is None:
                    self.assertIn("did not report its native pending-pause probe result", result.stderr)
                    self.assertFalse(summary.exists())
                    continue
                reported = probe.removeprefix("DIRGE_NATIVE_PENDING_PAUSE ").strip()
                self.assertIn(f"{annotation} title=Native pending pause::{reported}", result.stdout)
                self.assertIn(reported, summary.read_text())


if __name__ == "__main__":
    unittest.main()
