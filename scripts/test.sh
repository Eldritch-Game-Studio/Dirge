#!/usr/bin/env bash
# Source-owned test entrypoint invoked only after trusted exact-head admission.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet test tests/Dirge.Model.Tests -nologo
python3 -m unittest discover -s tests/scripts -v
