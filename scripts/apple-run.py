#!/usr/bin/env python3
"""Run foreground work under the shared Apple producer lock; pass through elsewhere."""
import os
from pathlib import Path
import signal
import subprocess
import sys


def main():
    if len(sys.argv) < 2:
        raise SystemExit("usage: apple-run.py COMMAND [ARG ...]")
    if sys.platform != "darwin":
        os.execvp(sys.argv[1], sys.argv[1:])
    import fcntl

    home = Path.home()
    os.environ["PATH"] = f"{home}/.dotnet:{home}/.local/share/eldritch-macos/tools/bin:" + os.environ["PATH"]
    os.environ["DOTNET_ROOT"] = str(home / ".dotnet")
    os.environ["DOTNET_ROOT_ARM64"] = str(home / ".dotnet")
    lock = home / ".local/state/eldritch-macos/producer.lock"
    lock.parent.mkdir(parents=True, exist_ok=True)
    os.environ["DOTNET_ROOT_X64"] = str(home / ".dotnet-x64")
    with lock.open("a") as handle:
        fcntl.flock(handle, fcntl.LOCK_EX)
        child = None
        pending = []

        def forward(signum, _frame):
            pending.append(signum)
            if child is not None:
                try:
                    os.killpg(child.pid, signum)
                except ProcessLookupError:
                    pass

        for signum in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
            signal.signal(signum, forward)
        child = subprocess.Popen(sys.argv[1:], start_new_session=True)
        for signum in tuple(pending):
            forward(signum, None)
        status = child.wait()
        if pending:
            # A shell may exit before its descendants. Stop the whole foreground
            # group before relinquishing the producer's lock on cancellation.
            try:
                os.killpg(child.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            status = -pending[-1]
    if status < 0:
        signal.signal(-status, signal.SIG_DFL)
        os.kill(os.getpid(), -status)
    raise SystemExit(status)


if __name__ == "__main__":
    main()
