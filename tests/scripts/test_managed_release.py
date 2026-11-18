"""Exercise the actual privileged publisher shell with recorded GitHub API responses."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]
REPOSITORY = "Eldritch-Game-Studio/Dirge"
SHA = "a" * 40
TAG = "managed-" + SHA[:12]
ZIP = f"dirge-managed-{SHA}.zip"

GH = """#!/usr/bin/env python3
import json, os, sys
from pathlib import Path
args = sys.argv[1:]
with open(os.environ['GH_LOG'], 'a') as log:
    log.write(json.dumps(args) + '\\n')
if args[0] == 'api':
    responses = json.loads(Path(os.environ['GH_RESPONSES']).read_text())
    response = responses[args[1]]
    if isinstance(response, int):
        print(f'gh: request failed (HTTP {response})', file=sys.stderr)
        sys.exit(1)
    print(json.dumps(response))
elif args[:2] not in (['release', 'create'], ['release', 'edit']):
    sys.exit('Unexpected mutation: ' + repr(args))
"""


def publisher_shell():
    workflow = (ROOT / ".github/workflows/ci-local.yml").read_text()
    step = workflow.split("      - name: Publish the managed-<sha12> release\n", 1)[1]
    return textwrap.dedent(step.split("        run: |\n", 1)[1].split("      - name: Notify consumers", 1)[0])


class ManagedReleaseTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        package = self.root / ZIP
        package.write_bytes(b"deterministic package fixture")
        self.digest = hashlib.sha256(package.read_bytes()).hexdigest()
        self.proof = {
            "schema": "eldritch.managed-provenance/1", "package": "dirge", "repository": REPOSITORY,
            "branch": "managed/1.0.x", "sha": SHA, "release_tag": TAG, "zip": ZIP,
            "zip_sha256": self.digest, "upstream": {"necronomidom": {"sha": "b" * 40}},
            "ref": "refs/heads/managed/1.0.x", "run_id": 10, "qualified_run_id": 10, "run_attempt": 1,
        }
        candidate = dict(self.proof, run_id=20, qualified_run_id=20)
        (self.root / "provenance.json").write_text(json.dumps(candidate))
        self.release_path = f"repos/{REPOSITORY}/releases/tags/{TAG}"
        self.proof_path = f"repos/{REPOSITORY}/releases/assets/42"
        self.run_path = f"repos/{REPOSITORY}/actions/runs/10"
        self.release = {"assets": [
            {"name": ZIP, "digest": "sha256:" + self.digest, "id": 41},
            {"name": "provenance.json", "id": 42},
        ]}
        self.run = {
            "id": 10, "head_sha": SHA, "repository": {"full_name": REPOSITORY},
            "path": ".github/workflows/ci.yml", "event": "push", "head_branch": "managed/1.0.x",
            "status": "completed", "conclusion": "success", "run_attempt": 1,
        }
        self.responses = {self.release_path: self.release, self.proof_path: self.proof, self.run_path: self.run}
        binary = self.root / "bin"
        binary.mkdir()
        (binary / "gh").write_text(GH)
        (binary / "gh").chmod(0o755)
        self.log = self.root / "calls.jsonl"
        self.output = self.root / "outputs"
        self.environment = dict(os.environ, PATH=f"{binary}{os.pathsep}{os.environ['PATH']}",
                                GH_LOG=str(self.log), GH_RESPONSES=str(self.root / "responses.json"),
                                ARTIFACT=str(self.root), GH_REPO=REPOSITORY, REVISION=SHA, TAGGED="false",
                                GITHUB_OUTPUT=str(self.output), GITHUB_RUN_ID="20", QUALIFICATION="success",
                                GITHUB_SERVER_URL="https://github.com")

    def publish(self, tagged=False):
        (self.root / "responses.json").write_text(json.dumps(self.responses))
        self.log.unlink(missing_ok=True)
        self.output.unlink(missing_ok=True)
        result = subprocess.run(["bash", "--noprofile", "--norc", "-eo", "pipefail", "-c", publisher_shell()],
                                env=dict(self.environment, TAGGED=str(tagged).lower()), text=True, capture_output=True)
        calls = [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []
        return result, calls

    def assert_rejected(self, tagged=True):
        result, calls = self.publish(tagged)
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(self.output.exists(), "Failed proof must not expose notification outputs")
        self.assertFalse(any(call[0] == "release" for call in calls), calls)

    def test_identical_live_release_is_reused_without_asset_mutation(self):
        result, calls = self.publish()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(calls, [["api", self.release_path],
                                 ["api", self.proof_path, "-H", "Accept: application/octet-stream"],
                                 ["api", self.run_path]])
        self.assertEqual(self.output.read_text(), f"sha={SHA}\ntag={TAG}\ndigest={self.digest}\n")

    def test_own_running_rerun_reuses_proof_only_after_qualification(self):
        self.environment["GITHUB_RUN_ID"] = "10"
        self.run.update(status="in_progress", conclusion=None, run_attempt=2)
        result, calls = self.publish()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(self.output.exists())
        self.assertFalse(any(call[0] == "release" for call in calls))

    def test_other_running_run_is_rejected_even_after_current_qualification(self):
        self.run.update(status="in_progress", conclusion=None)
        self.assert_rejected()

    def test_own_running_run_without_successful_qualification_is_rejected(self):
        self.environment["GITHUB_RUN_ID"] = "10"
        self.run.update(status="in_progress", conclusion=None)
        for qualification in ("failure", "cancelled", "skipped", ""):
            with self.subTest(qualification=qualification):
                self.environment["QUALIFICATION"] = qualification
                self.assert_rejected()

    def test_same_run_exception_preserves_all_source_and_workflow_identity_checks(self):
        self.environment["GITHUB_RUN_ID"] = "10"
        original = dict(self.run, status="in_progress", conclusion=None)
        changes = {"head_sha": "c" * 40, "repository": {"full_name": "other/repo"},
                   "path": ".github/workflows/other.yml", "event": "workflow_dispatch",
                   "head_branch": "feature", "conclusion": "failure", "status": "queued"}
        for key, value in changes.items():
            with self.subTest(field=key):
                self.responses[self.run_path] = {**original, key: value}
                self.assert_rejected()

    def test_deleted_run_fails_before_promotion_or_notification(self):
        self.responses[self.run_path] = 404
        self.assert_rejected()

    def test_missing_or_noncanonical_assets_are_never_repaired_in_place(self):
        for assets in ([], self.release["assets"][:1], self.release["assets"][1:],
                       self.release["assets"] + [self.release["assets"][1]],
                       [{"name": ZIP, "id": 41}, self.release["assets"][1]],
                       [{"name": ZIP, "digest": "sha256:" + "0" * 64}, self.release["assets"][1]]):
            with self.subTest(assets=assets):
                self.responses[self.release_path] = {"assets": assets}
                self.assert_rejected()

    def test_unavailable_or_malformed_provenance_is_rejected(self):
        for proof in (404, {}, None, {**self.proof, "ref": None}):
            with self.subTest(proof=proof):
                self.responses[self.proof_path] = proof
                self.assert_rejected()

    def test_provenance_identity_and_same_run_policy_are_enforced(self):
        changes = {"schema": "unknown", "package": "other", "repository": "other/repo", "branch": "main",
                   "sha": "c" * 40, "release_tag": "other", "zip": "other.zip", "zip_sha256": "0" * 64,
                   "upstream": {}, "ref": "refs/heads/feature", "run_id": "10", "qualified_run_id": 9,
                   "run_attempt": 0}
        for key, value in changes.items():
            with self.subTest(field=key):
                self.responses[self.proof_path] = {**self.proof, key: value}
                self.assert_rejected()

    def test_live_run_identity_event_status_and_attempt_are_enforced(self):
        changes = {"id": 11, "head_sha": "c" * 40, "repository": {"full_name": "other/repo"},
                   "path": ".github/workflows/other.yml", "event": "workflow_dispatch", "head_branch": "feature",
                   "status": "in_progress", "conclusion": "failure", "run_attempt": 0}
        for key, value in changes.items():
            with self.subTest(field=key):
                self.responses[self.run_path] = {**self.run, key: value}
                self.assert_rejected()

    def test_healthy_branch_release_can_be_promoted_without_replacing_assets(self):
        result, calls = self.publish(tagged=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(calls[-1], ["release", "edit", TAG, "--prerelease=false"])
        self.assertFalse(any(call[:2] == ["release", "upload"] for call in calls))

    def test_healthy_existing_tag_push_proof_is_accepted(self):
        self.proof["ref"] = "refs/tags/v1.0.6"
        self.run["head_branch"] = "v1.0.6"
        result, _ = self.publish()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_only_real_release_404_allows_new_release_creation(self):
        self.responses[self.release_path] = 404
        result, calls = self.publish()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        creation = calls[-1]
        self.assertEqual(creation[:3], ["release", "create", TAG])
        self.assertIn(str(self.root / ZIP), creation)
        self.assertIn(str(self.root / "provenance.json"), creation)
        self.assertIn("--prerelease", creation)

    def test_release_lookup_permission_and_transport_errors_fail_closed(self):
        for status in (403, 429, 500):
            with self.subTest(status=status):
                self.responses[self.release_path] = status
                self.assert_rejected()

    def test_local_digest_mismatch_is_rejected_before_any_github_call(self):
        (self.root / ZIP).write_bytes(b"changed package")
        self.assert_rejected()
        self.assertFalse(self.log.exists())


if __name__ == "__main__":
    unittest.main()
