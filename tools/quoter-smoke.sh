#!/usr/bin/env bash
# tools/quoter-smoke.sh -- thin dispatcher to tools/quoter-smoke.py.
# Invoked by: humans during HUMAN-UAT re-run; operator-side smoke-gate in later phases.
# Exit code: 0 = pass; non-zero = fail. Python 3.12+ required on PATH.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

if ! command -v python3 >/dev/null 2>&1; then
  echo "::error::python3 not found on PATH. Install Python 3.12+." >&2
  exit 2
fi

exec python3 tools/quoter-smoke.py "$@"
