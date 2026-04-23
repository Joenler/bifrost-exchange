#!/usr/bin/env bash
# Negative test: plants a forbidden marker in a temp file under src/ and asserts
# tools/scrub-gsd.sh exits non-zero. Cleans up regardless of outcome.

set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

CANARY="src/exchange/_ScrubGsdCanary.cs"
cleanup() { rm -f "$CANARY"; }
trap cleanup EXIT

# Plant a marker (use concatenation so this script itself is not a canary)
echo "// phase-02 canary marker for EX-06 negative test" > "$CANARY"

if bash tools/scrub-gsd.sh; then
  echo "FAIL: scrub-gsd.sh passed with a planted marker in $CANARY" >&2
  exit 1
fi

echo "OK: scrub-gsd.sh correctly rejects planted GSD marker"
