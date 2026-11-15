#!/usr/bin/env bash
# Source-owned portable tests for the managed 1.0.x line; main's reusable CI runs this entrypoint.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet test tests/Dirge.Model.Tests -nologo
python3 -m unittest discover -s tests/scripts -v
