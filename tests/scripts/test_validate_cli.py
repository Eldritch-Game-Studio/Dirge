"""Actual stock-Godot CLI behavior; smoke supplies GODOT after native import."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


@unittest.skipUnless(os.environ.get("GODOT"), "requires imported project and explicit standard GODOT; exercised by smoke.sh")
class ValidationCommandTests(unittest.TestCase):
    def invoke(self, *arguments):
        return subprocess.run(["bash", str(ROOT / "scripts/validate.sh"), *map(str, arguments)], cwd=ROOT, text=True, capture_output=True, timeout=180)

    def test_authored_demo_json_contract(self):
        result = self.invoke("demo/demo.dirge.json", "--json")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        diagnostics = json.loads(result.stdout)
        self.assertIsInstance(diagnostics, list)
        self.assertFalse(any(item["severity"] == "error" for item in diagnostics))

    def test_semantic_errors_have_exit_one_and_structured_diagnostics(self):
        project = json.loads((ROOT / "demo/demo.dirge.json").read_text())
        project["mixer"]["buses"][0]["faderIndex"] = -1
        with tempfile.TemporaryDirectory(dir=ROOT) as directory:
            path = Path(directory) / "invalid.dirge.json"
            path.write_text(json.dumps(project))
            result = self.invoke(path.relative_to(ROOT), "--json")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        errors = [item for item in json.loads(result.stdout) if item["code"] == "bus-fader-index-negative"]
        self.assertEqual(len(errors), 1)
        self.assertEqual(errors[0]["severity"], "error")
        self.assertEqual(set(errors[0]), {"severity", "code", "message", "subject"})

    def test_parse_usage_and_missing_file_exit_two(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as directory:
            path = Path(directory) / "broken.dirge.json"
            path.write_text("{")
            for arguments in ((path.relative_to(ROOT),), ("--unknown",), ("absent.dirge.json",)):
                with self.subTest(arguments=arguments):
                    result = self.invoke(*arguments)
                    self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
                    self.assertIn("dirge-validate:", result.stderr)

    def test_help_is_success_without_a_project_file(self):
        result = self.invoke("--help")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("Exit 0: no errors", result.stdout)


if __name__ == "__main__":
    unittest.main()
