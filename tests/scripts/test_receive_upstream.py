"""The receiver's PR transitions against a real local origin; gh and package verification are recorded stand-ins."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
OLD, PENDING, NEW = "a" * 40, "c" * 40, "b" * 40


def lock(sha):
    return {"necronomidom": {"repository": "Eldritch-Game-Studio/NecronomiDOM", "sha": sha, "release_tag": "managed-" + sha[:12],
                             "zip": f"necronomidom-managed-{sha}.zip", "zip_sha256": "d" * 64}}


GH = """#!/usr/bin/env python3
import json, os, sys
log = os.environ["GH_LOG"]
created = os.path.exists(log) and any(json.loads(line)[:2] == ["pr", "create"] for line in open(log))
with open(log, "a") as output:
    output.write(json.dumps(sys.argv[1:]) + "\\n")
args = sys.argv[1:]
if args[:2] == ["pr", "list"]:
    print("8" if created else os.environ.get("OPEN_PR", ""))
elif args[0] == "api" and "/compare/" in args[1]:
    print(os.environ["COMPARE_STATUS"])
"""

# Stands in for verification: answers from VERIFY (one outcome per call) and writes the lock the real script would.
VERIFY = """#!/usr/bin/env python3
import json, os, sys
from pathlib import Path
state = Path(os.environ["VERIFY_STATE"])
calls = int(state.read_text()) if state.exists() else 0
state.write_text(str(calls + 1))
outcome = os.environ["VERIFY"].split(",")[calls]
if outcome == "fail":
    sys.exit("update-dependency-lock: provenance branch is 'feature', expected 'main'")
sha = sys.argv[sys.argv.index("--sha") + 1]
if outcome == "newer":
    Path("dependency-lock.json").write_text(os.environ["NEW_LOCK"])
print(json.dumps({"changed": outcome == "newer", "relation": outcome, "sha": sha}, indent=2))
"""


class ReceiverTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.temp = Path(temporary.name)
        self.origin = self.temp / "origin.git"
        self.work = self.temp / "work"
        self.git("init", "-q", "--bare", str(self.origin), cwd=self.temp)
        self.work.mkdir()
        self.git("init", "-q", "-b", "managed/1.0.x")
        (self.work / "scripts").mkdir()
        shutil.copy2(ROOT / "scripts/receive-upstream.sh", self.work / "scripts/receive-upstream.sh")
        (self.work / "scripts/update-dependency-lock.py").write_text(VERIFY)
        (self.work / "dependency-lock.json").write_text(json.dumps(lock(OLD), indent=2) + "\n")
        self.git("add", ".")
        self.commit("base")
        self.git("remote", "add", "origin", self.origin.as_uri())
        self.git("push", "-q", "origin", "managed/1.0.x")
        tools = self.temp / "tools"
        tools.mkdir()
        (tools / "gh").write_text(GH)
        (tools / "gh").chmod(0o755)
        self.log = self.temp / "gh.log"
        self.environment = dict(os.environ, PATH=f"{tools}{os.pathsep}{os.environ['PATH']}", GH_LOG=str(self.log),
                                VERIFY_STATE=str(self.temp / "verify-state"), RUNNER_TEMP=str(self.temp),
                                NEW_LOCK=json.dumps(lock(NEW), indent=2) + "\n", COMPARE_STATUS="behind",
                                PACKAGE="necronomidom", SHA=NEW, RELEASE_TAG="managed-" + NEW[:12], ZIP_SHA256="d" * 64,
                                GIT_AUTHOR_NAME="t", GIT_AUTHOR_EMAIL="t@example.com",
                                GIT_COMMITTER_NAME="t", GIT_COMMITTER_EMAIL="t@example.com")

    def git(self, *args, cwd=None):
        return subprocess.run(["git", *args], cwd=cwd or self.work, check=True, capture_output=True, text=True).stdout.strip()

    def commit(self, message):
        self.git("-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-a", "-m", message)

    def pending_pr(self, sha):
        """An open upstream/necronomidom PR whose branch already locks `sha`."""
        self.git("checkout", "-q", "-b", "upstream/necronomidom")
        (self.work / "dependency-lock.json").write_text(json.dumps(lock(sha), indent=2) + "\n")
        self.commit("pending")
        self.git("push", "-q", "origin", "upstream/necronomidom")
        self.git("checkout", "-q", "managed/1.0.x")
        self.environment["OPEN_PR"] = "7"

    def receive(self, verify, **environment):
        result = subprocess.run(["bash", "scripts/receive-upstream.sh"], cwd=self.work, capture_output=True, text=True,
                                env=dict(self.environment, VERIFY=verify, **environment))
        calls = [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []
        return result, calls

    def remote_lock(self, branch):
        try:
            return json.loads(self.git("show", f"{branch}:dependency-lock.json", cwd=self.origin))["necronomidom"]["sha"]
        except subprocess.CalledProcessError:
            return None

    def test_newer_package_opens_one_pr_with_auto_merge_on_the_pushed_head(self):
        result, calls = self.receive("newer")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.remote_lock("upstream/necronomidom"), NEW)
        self.assertEqual(self.remote_lock("managed/1.0.x"), OLD)
        head = self.git("rev-parse", "upstream/necronomidom", cwd=self.origin)
        self.assertEqual(self.git("rev-parse", "upstream/necronomidom^", cwd=self.origin),
                         self.git("rev-parse", "managed/1.0.x", cwd=self.origin))
        changed = self.git("diff", "--name-only", "managed/1.0.x", "upstream/necronomidom", cwd=self.origin)
        self.assertEqual(changed, "dependency-lock.json")
        self.assertTrue(any(call[:2] == ["pr", "create"] and "--base" in call and "managed/1.0.x" in call for call in calls))
        merge = [call for call in calls if call[:2] == ["pr", "merge"]][-1]
        self.assertEqual(merge[2], "8")
        self.assertIn("--auto", merge)
        self.assertEqual(merge[merge.index("--match-head-commit") + 1], head)

    def test_newer_package_replaces_the_open_prs_older_lock_in_place(self):
        self.pending_pr(PENDING)
        result, calls = self.receive("newer,newer")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.remote_lock("upstream/necronomidom"), NEW)
        self.assertFalse(any(call[:2] == ["pr", "create"] for call in calls))
        self.assertTrue(any(call[:3] == ["pr", "edit", "7"] for call in calls))

    def test_package_older_than_the_open_pr_keeps_its_newer_lock(self):
        self.pending_pr(PENDING)
        before = self.git("rev-parse", "upstream/necronomidom", cwd=self.origin)
        result, calls = self.receive("newer,older")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.git("rev-parse", "upstream/necronomidom", cwd=self.origin), before)
        self.assertIn(["pr", "merge", "7", "--repo", "Eldritch-Game-Studio/Dirge", "--auto", "--merge"], calls)

    def test_already_locked_package_changes_nothing_and_closes_a_superseded_pr(self):
        self.pending_pr(PENDING)
        result, calls = self.receive("same", COMPARE_STATUS="behind")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(any(call[:3] == ["pr", "close", "7"] for call in calls))
        self.assertEqual(self.remote_lock("managed/1.0.x"), OLD)

    def test_failed_verification_fails_without_pushing(self):
        result, calls = self.receive("fail")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("provenance branch", result.stderr)
        self.assertIsNone(self.remote_lock("upstream/necronomidom"))
        self.assertFalse(any(call[:2] in (["pr", "create"], ["pr", "merge"]) for call in calls))

    def test_rejects_malformed_or_foreign_payloads_before_any_work(self):
        for environment in ({"PACKAGE": "dirge"}, {"RELEASE_TAG": "v1.1.6"}, {"SHA": "HEAD"}):
            with self.subTest(**environment):
                result, calls = self.receive("newer", **environment)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(calls, [])


if __name__ == "__main__":
    unittest.main()
