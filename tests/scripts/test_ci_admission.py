from pathlib import Path
import re
from types import SimpleNamespace
import unittest

WORKFLOWS = Path(__file__).resolve().parents[2] / ".github/workflows"
WORKFLOW = WORKFLOWS / "ci-local.yml"
LINE = "refs/heads/managed/1.0.x"


class AdmissionConcurrencyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.workflow = WORKFLOW.read_text()
        cls.concurrency = cls.workflow.split("\nconcurrency:\n", 1)[1].split("\nenv:\n", 1)[0]
        cls.expression = re.search(r"group: local-ci-\$\{\{ (.+) \}\}", cls.concurrency).group(1)

    def group(self, event, number=0, approved_pr="", ref=LINE):
        # This expression uses only boolean selection, equality and format; evaluate
        # the actual workflow expression rather than a second copy of its policy.
        expression = self.expression.replace("&&", "and").replace("||", "or")
        github = SimpleNamespace(
            event_name=event, ref=ref,
            event=SimpleNamespace(pull_request=SimpleNamespace(number=number)),
        )
        return eval(expression, {"__builtins__": {}}, {
            "github": github, "inputs": SimpleNamespace(approved_pr=approved_pr),
            "format": lambda pattern, value: pattern.format(value),
        })

    def test_reviewed_dispatch_shares_pr_group(self):
        self.assertEqual(self.group("pull_request", number=5), "pr-5")
        self.assertEqual(self.group("workflow_dispatch", approved_pr="5"), "pr-5")
        self.assertNotEqual(self.group("pull_request", number=6), "pr-5")

    def test_line_dispatch_shares_line_push_group(self):
        self.assertEqual(self.group("workflow_dispatch"), self.group("push"))
        self.assertEqual(self.group("push"), LINE)

    def test_release_tags_remain_separate_from_line(self):
        self.assertEqual(self.group("push", ref="refs/tags/v1.0.3"), "refs/tags/v1.0.3")
        self.assertNotEqual(self.group("push", ref="refs/tags/v1.0.3"), self.group("push"))

    def test_line_runs_its_own_reusable_workflow(self):
        caller = (WORKFLOWS / "ci.yml").read_text()
        self.assertIn("uses: Eldritch-Game-Studio/Dirge/.github/workflows/ci-local.yml@managed/1.0.x\n", caller)
        self.assertRegex(caller, r"(?m)^    branches: \[managed/1\.0\.x\]$")
        trust = self.workflow.split("\n  trust:\n", 1)[1].split("\n  test:\n", 1)[0]
        # Dispatches and pushes are admitted only from this line's branch (plus release tags).
        self.assertIn(f"context.ref !== '{LINE}'", trust)
        self.assertIn(f"context.ref === '{LINE}' || context.ref.startsWith('refs/tags/v')", trust)
        self.assertNotIn("refs/heads/main", trust)

    def test_new_events_do_not_cancel_active_native_work(self):
        self.assertRegex(self.concurrency, r"(?m)^  cancel-in-progress: false$")
        self.assertNotIn("github.run_id", self.concurrency)

    def test_stale_head_validation_remains_before_checkout(self):
        trust = self.workflow.split("\n  trust:\n", 1)[1].split("\n  test:\n", 1)[0]
        self.assertIn("pr.head.sha !== context.payload.pull_request.head.sha", trust)
        self.assertIn("pr.head.sha !== process.env.APPROVED_HEAD", trust)
        self.assertNotIn("actions/checkout", trust)
        for name in ("native-linux", "native-windows", "native-macos"):
            # Select the complete indented job until the next sibling job.
            job = re.split(r"\n  [a-z][a-z-]*:\n", self.workflow.split(f"\n  {name}:\n", 1)[1], maxsplit=1)[0]
            self.assertIn("needs: trust", job)


if __name__ == "__main__":
    unittest.main()
