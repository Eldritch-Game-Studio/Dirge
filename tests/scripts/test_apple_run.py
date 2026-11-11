import fcntl
import os
from pathlib import Path
import select
import signal
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / "scripts/apple-run.py"
# Exercise the POSIX lock protocol on either producer without starting native work.
DRIVER = "import runpy, subprocess, sys; sys.platform = 'darwin'; sys.argv = sys.argv[1:]; runpy.run_path(sys.argv[0], run_name='__main__')"


class AppleProducerTests(unittest.TestCase):
    def launch(self, home, code):
        return subprocess.Popen(
            [sys.executable, "-c", DRIVER, str(SCRIPT), sys.executable, "-c", code],
            env={**os.environ, "HOME": home}, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        )

    def test_preserves_command_exit_code(self):
        with tempfile.TemporaryDirectory() as home:
            child = self.launch(home, "raise SystemExit(17)")
            child.communicate(timeout=10)
            self.assertEqual(child.returncode, 17)

    def test_holds_lock_and_forwards_termination(self):
        with tempfile.TemporaryDirectory() as home:
            child = self.launch(home, "import time; print('ready', flush=True); time.sleep(30)")
            try:
                readable, _, _ = select.select([child.stdout], [], [], 10)
                self.assertTrue(readable, "child did not start")
                self.assertEqual(child.stdout.readline(), b"ready\n")
                lock = Path(home) / ".local/state/eldritch-macos/producer.lock"
                with lock.open("a") as handle:
                    with self.assertRaises(BlockingIOError):
                        fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
                    child.send_signal(signal.SIGTERM)
                    child.communicate(timeout=10)
                    self.assertEqual(child.returncode, -signal.SIGTERM)
                    fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
            finally:
                if child.poll() is None:
                    child.send_signal(signal.SIGTERM)
                    child.communicate(timeout=10)


if __name__ == "__main__":
    unittest.main()
