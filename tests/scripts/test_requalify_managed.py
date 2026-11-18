import copy
import importlib.util
import io
import json
import os
import re
import shutil
from pathlib import Path
import subprocess
import tarfile
import textwrap
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("requalification", ROOT / "scripts/requalify-managed.py")
requalification = importlib.util.module_from_spec(spec)
spec.loader.exec_module(requalification)
SHA = "a" * 40
RUN = 123
PRODUCER = 456
PREFIX = "repos/" + requalification.REPOSITORY


def node_binary():
    installed = shutil.which("node")
    if installed:
        return installed
    # Self-hosted runners already carry this runtime for JavaScript actions.
    # Do not install another runtime or skip the publisher's execution tests.
    workspace = os.environ.get("RUNNER_WORKSPACE")
    if workspace:
        runtime = Path(workspace).parents[1] / "externals/node24/bin/node"
        if runtime.is_file() and os.access(runtime, os.X_OK):
            return str(runtime)
    raise RuntimeError("Publisher execution tests require executable Node.js on PATH or the runner-bundled Node24")


def bundle(files):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, "w") as archive:
        for name, content in files.items():
            archive.writestr(name, content)
    return stream.getvalue()


class RequalificationTests(unittest.TestCase):
    def setUp(self):
        self.files = {"Dirge.cs": b"// exact source\n"}
        self.lock = {"necronomidom": {"sha": "b" * 40, "zip_sha256": "c" * 64}}
        self.manifest = {
            "schema": "eldritch.managed-addon/1", "addon": "dirge", "revision": SHA,
            "repository": "https://github.com/" + requalification.REPOSITORY, "profile": "managed-host",
            "files": {name: requalification.digest(data) for name, data in self.files.items()},
        }
        self.name = f"dirge-managed-{SHA}.zip"
        self.package = bundle({"addons/dirge/Dirge.cs": self.files["Dirge.cs"],
                               "addon-package.json": json.dumps(self.manifest)})
        self.original = {
            "schema": "eldritch.managed-provenance/1", "package": "dirge",
            "repository": requalification.REPOSITORY, "branch": requalification.BRANCH, "sha": SHA,
            "ref": "refs/heads/" + requalification.BRANCH, "run_id": 99, "run_attempt": 1,
            "qualified_run_id": 99, "release_tag": "managed-" + SHA[:12], "zip": self.name,
            "zip_sha256": requalification.digest(self.package), "upstream": self.lock,
        }
        self.original_bytes = json.dumps(self.original).encode()
        self.run = {
            "id": RUN, "head_sha": SHA, "repository": {"full_name": requalification.REPOSITORY},
            "path": requalification.WORKFLOW, "head_branch": requalification.BRANCH,
            "event": "workflow_dispatch", "status": "completed", "conclusion": "success", "run_attempt": 1,
        }
        self.jobs = [{"name": name, "status": "completed", "conclusion": "success", "run_attempt": 1}
                     for name in requalification.REQUIRED_JOBS]
        self.archive = bundle({self.name: self.package})
        self.artifact = {"id": 3, "name": "dirge-managed", "expired": False,
                         "digest": "sha256:" + requalification.digest(self.archive),
                         "workflow_run": {"id": RUN, "head_sha": SHA, "head_branch": requalification.BRANCH}}
        self.release = {"id": 77, "tag_name": self.original["release_tag"], "draft": False, "assets": [
            {"id": 1, "name": "provenance.json", "digest": "sha256:" + requalification.digest(self.original_bytes)},
            {"id": 2, "name": self.name, "digest": "sha256:" + requalification.digest(self.package)},
        ]}
        source = io.BytesIO()
        with tarfile.open(fileobj=source, mode="w") as archive:
            for name, content in self.files.items():
                member = tarfile.TarInfo("addons/dirge/" + name)
                member.size = len(content)
                archive.addfile(member, io.BytesIO(content))
        self.source = source.getvalue()
        self.calls = []

    def api(self, path, binary=False):
        self.calls.append(path)
        routes = {
            f"{PREFIX}/actions/runs/{RUN}": self.run,
            f"{PREFIX}/actions/runs/{RUN}/attempts/1/jobs?per_page=100&page=1": {"jobs": self.jobs},
            f"{PREFIX}/actions/runs/{RUN}/artifacts?per_page=100&page=1": {"artifacts": [self.artifact]},
            f"{PREFIX}/releases/tags/{self.original['release_tag']}": self.release,
            f"{PREFIX}/releases/assets/1": self.original_bytes,
            f"{PREFIX}/releases/assets/2": self.package,
            f"{PREFIX}/actions/artifacts/3/zip": self.archive,
        }
        if path not in routes:
            raise AssertionError("Unexpected API request (including unavailable original run): " + path)
        return copy.deepcopy(routes[path])

    def prepare(self):
        with patch.object(requalification.subprocess, "run") as command, patch.object(
                requalification.subprocess, "check_output", side_effect=[json.dumps(self.lock).encode(), self.source]):
            result = requalification.prepare(RUN, PRODUCER, 1, root=ROOT, api=self.api)
        command.assert_called_once_with(
            ["git", "merge-base", "--is-ancestor", SHA, "origin/managed/1.0.x"], cwd=ROOT, check=True)
        return result

    def test_uses_runner_javascript_runtime_when_node_is_not_on_path(self):
        with patch.object(shutil, "which", return_value=None), patch.dict(
                os.environ, {"RUNNER_WORKSPACE": "/runner/_work/Dirge"}), patch.object(
                Path, "is_file", return_value=True), patch.object(os, "access", return_value=True):
            self.assertEqual(node_binary(), "/runner/externals/node24/bin/node")

    def test_missing_runtime_is_a_clear_failure_not_a_skip(self):
        with patch.object(shutil, "which", return_value=None), patch.dict(os.environ, {}, clear=True):
            with self.assertRaisesRegex(RuntimeError, "require executable Node.js"):
                node_binary()

    def test_runner_runtime_must_be_an_executable_file(self):
        for exists, executable in ((False, True), (True, False)):
            with self.subTest(exists=exists, executable=executable), patch.object(
                    shutil, "which", return_value=None), patch.dict(
                    os.environ, {"RUNNER_WORKSPACE": "/runner/_work/Dirge"}), patch.object(
                    Path, "is_file", return_value=exists), patch.object(os, "access", return_value=executable):
                with self.assertRaisesRegex(RuntimeError, "require executable Node.js"):
                    node_binary()

    def test_binary_endpoints_use_their_own_media_negotiation(self):
        with patch.object(requalification.subprocess, "check_output", return_value=b"PK") as request:
            self.assertEqual(requalification.gh(f"{PREFIX}/actions/artifacts/3/zip", True), b"PK")
            request.assert_called_once_with(["gh", "api", f"{PREFIX}/actions/artifacts/3/zip"])
        with patch.object(requalification.subprocess, "check_output", return_value=b"{}") as request:
            self.assertEqual(requalification.gh(f"{PREFIX}/releases/assets/1", True), b"{}")
            request.assert_called_once_with(
                ["gh", "api", f"{PREFIX}/releases/assets/1", "-H", "Accept: application/octet-stream"])

    def test_independent_full_evidence_binds_original_bytes_and_actual_artifact(self):
        proof = self.prepare()
        self.assertEqual(proof["schema"], "eldritch.managed-requalification/1")
        self.assertEqual((proof["run_id"], proof["qualified_run_id"]), (PRODUCER, RUN))
        self.assertEqual(proof["original_provenance"]["sha256"], requalification.digest(self.original_bytes))
        self.assertEqual(proof["qualified_artifact"]["sha256"], requalification.digest(self.archive))
        for key in ("sha", "zip", "zip_sha256", "upstream"):
            self.assertEqual(proof[key], self.original[key])
        self.assertNotIn(f"{PREFIX}/actions/runs/99", self.calls)

    def test_unfinished_failed_wrong_source_or_wrong_producer_run_rejected(self):
        for field, value in [("status", "in_progress"), ("conclusion", "failure"), ("head_branch", "main"),
                             ("path", ".github/workflows/other.yml"), ("event", "pull_request"),
                             ("repository", {"full_name": "fork/Dirge"}), ("head_sha", "bad")]:
            with self.subTest(field=field):
                run = {**self.run, field: value}
                with self.assertRaises(ValueError):
                    requalification.verify_run(run, self.jobs, RUN)

    def test_every_full_job_is_mandatory_for_exact_attempt(self):
        for index, name in enumerate(requalification.REQUIRED_JOBS):
            for mutation in ({"conclusion": "skipped"}, {"status": "in_progress"}, {"run_attempt": 2}):
                with self.subTest(name=name, mutation=mutation):
                    jobs = copy.deepcopy(self.jobs)
                    jobs[index].update(mutation)
                    with self.assertRaisesRegex(ValueError, "FULL exact-attempt"):
                        requalification.verify_run(self.run, jobs, RUN)
            with self.subTest(missing=name), self.assertRaises(ValueError):
                requalification.verify_run(self.run, self.jobs[:index] + self.jobs[index + 1:], RUN)
        with self.assertRaises(ValueError):
            requalification.verify_run(self.run, self.jobs + [self.jobs[0]], RUN)

    def test_same_run_cannot_claim_independent_requalification(self):
        with self.assertRaises(ValueError):
            requalification.prepare(RUN, RUN, 1, api=self.api)

    def test_original_necronomidom_pin_cannot_change(self):
        self.lock["necronomidom"]["sha"] = "d" * 40
        with self.assertRaisesRegex(ValueError, "locked NecronomiDOM"):
            self.prepare()

    def test_original_provenance_asset_digest_required(self):
        self.release["assets"][0]["digest"] = "sha256:" + "0" * 64
        with self.assertRaisesRegex(ValueError, "asset digests"):
            self.prepare()

    def test_missing_original_and_duplicate_assets_rejected(self):
        for assets in ([self.release["assets"][1]], self.release["assets"] + [self.release["assets"][0]]):
            with self.subTest(assets=assets), self.assertRaises(ValueError):
                requalification.asset({"assets": assets}, "provenance.json")

    def test_release_package_digest_required(self):
        self.release["assets"][1]["digest"] = None
        with self.assertRaisesRegex(ValueError, "asset digests"):
            self.prepare()

    def test_artifact_must_belong_to_exact_source_and_remain_live(self):
        for key, value in [("expired", True), ("workflow_run", {"id": RUN, "head_sha": "d" * 40})]:
            with self.subTest(key=key):
                before = copy.deepcopy(self.artifact)
                self.artifact[key] = value
                with self.assertRaisesRegex(ValueError, "source differs or expired"):
                    self.prepare()
                self.artifact = before

    def test_artifact_archive_digest_is_verified(self):
        self.artifact["digest"] = "sha256:" + "0" * 64
        with self.assertRaisesRegex(ValueError, "archive digest"):
            self.prepare()

    def test_qualifying_artifact_must_contain_byte_identical_package(self):
        self.archive = bundle({self.name: b"different ZIP bytes"})
        self.artifact["digest"] = "sha256:" + requalification.digest(self.archive)
        with self.assertRaisesRegex(ValueError, "not byte-identical"):
            self.prepare()

    def test_manifest_and_every_source_byte_verified(self):
        for files in ({"Dirge.cs": b"changed"}, {"extra.cs": b"extra"}, {}):
            with self.subTest(files=files), self.assertRaises(ValueError):
                requalification.verify_package(self.package, SHA, files)

    def publisher(self, mutation=None):
        proof = self.prepare()
        data = {"proof": proof, "original": self.original_bytes.decode(), "run": self.run,
                "jobs": self.jobs, "release": self.release, "artifact": self.artifact, "status": "ahead"}
        if mutation:
            mutation(data)
        workflow = (ROOT / ".github/workflows/ci-local.yml").read_text()
        job = workflow.split("\n  managed-requalification-release:\n", 1)[1]
        script = textwrap.dedent(job.split("          script: |\n", 1)[1])
        harness = r'''
const input = JSON.parse(require('fs').readFileSync(0, 'utf8'));
const AsyncFunction = Object.getPrototypeOf(async function(){}).constructor;
const data = input.data;
const calls = [];
const fakeRequire = name => name === 'fs' ? {
  readdirSync: () => ['provenance-456-1.json'],
  readFileSync: () => Buffer.from(JSON.stringify(data.proof))
} : require(name);
const context = {runId: 456, ref: 'refs/heads/managed/1.0.x', repo: {owner: 'Eldritch-Game-Studio', repo: 'Dirge'}};
const response = value => async () => ({data: value});
const github = {rest: {actions: {
  getWorkflowRun: response(data.run), listJobsForWorkflowRunAttempt: 'jobs', getArtifact: response(data.artifact)
}, repos: {
  compareCommitsWithBasehead: response({status: data.status}), getReleaseByTag: response(data.release),
  listReleaseAssets: 'assets', getReleaseAsset: response(Buffer.from(data.original)),
  uploadReleaseAsset: async args => { calls.push({name: args.name, release_id: args.release_id}); }
}}, paginate: async (method) => method === 'jobs' ? data.jobs : data.release.assets};
const summary = {addHeading: () => summary, addRaw: () => summary, write: async () => {}};
const env = {EVIDENCE: '/unused', GITHUB_RUN_ATTEMPT: '1', QUALIFIED_RUN: '123'};
(async () => {
  try {
    await new AsyncFunction('require', 'context', 'github', 'core', 'process', input.script)(
      fakeRequire, context, github, {summary}, {env});
    console.log(JSON.stringify({ok: true, calls}));
  } catch (error) {
    console.log(JSON.stringify({ok: false, error: error.message, calls}));
  }
})();
'''
        result = subprocess.run([node_binary(), "-e", harness], input=json.dumps({"data": data, "script": script}),
                                text=True, capture_output=True, check=True)
        return json.loads(result.stdout)

    def test_actual_publisher_appends_one_asset_without_replacing_or_dispatching(self):
        result = self.publisher()
        self.assertTrue(result["ok"], result)
        self.assertEqual(result["calls"], [{"name": "provenance-456-1.json", "release_id": 77}])

    def test_actual_publisher_fails_closed_before_any_write(self):
        mutations = [
            lambda data: data["run"].update(status="in_progress"),
            lambda data: data["run"].update(head_sha="d" * 40),
            lambda data: data["run"].update(run_attempt=2),
            lambda data: data["jobs"][0].update(conclusion="skipped"),
            lambda data: data.update(status="diverged"),
            lambda data: data["proof"]["original_provenance"].update(sha256="0" * 64),
            lambda data: data["release"]["assets"][1].update(digest="sha256:" + "0" * 64),
            lambda data: data["artifact"].update(expired=True),
            lambda data: data["release"]["assets"].append({"name": "provenance-456-1.json"}),
            lambda data: data["proof"]["upstream"].update(necronomidom={"sha": "d" * 40}),
            lambda data: data["proof"].update(qualified_run_id=99),
        ]
        for index, mutation in enumerate(mutations):
            with self.subTest(index=index):
                self.setUp()
                result = self.publisher(mutation)
                self.assertFalse(result["ok"], result)
                self.assertEqual(result["calls"], [])

    def test_recovery_dispatch_does_not_schedule_native_or_managed_rebuilds(self):
        workflow = (ROOT / ".github/workflows/ci-local.yml").read_text()
        caller = (ROOT / ".github/workflows/ci.yml").read_text()
        self.assertIn("github.event.inputs.requalification_run", workflow)
        self.assertNotIn("      requalification_run:", caller.split("    with:\n", 1)[1])
        for job in ("test", "validate", "native-linux", "native-windows", "native-macos", "managed-package"):
            section = re.split(r"\n  [a-z][a-z-]*:\n", workflow.split(f"\n  {job}:\n", 1)[1], maxsplit=1)[0]
            self.assertIn("needs.trust.outputs.requalification_run == ''", section)
        self.assertIn("always() && needs.trust.outputs.requalification_run == ''", workflow)
        publisher = workflow.split("\n  managed-requalification-release:\n", 1)[1]
        self.assertNotIn("actions/checkout", publisher)
        self.assertNotIn("--clobber", publisher)
        self.assertNotIn("dispatches", publisher)


if __name__ == "__main__":
    unittest.main()
