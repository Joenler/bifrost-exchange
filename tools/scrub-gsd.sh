#!/usr/bin/env bash
# tools/scrub-gsd.sh — single source of truth for GSD-marker scrubbing.
# Invoked by: .git/hooks/pre-commit (via tools/install-hooks.sh)
#             .github/workflows/ci.yml (ci-scrub-gsd job)
# Exits non-zero on any match.
#
# File selection is done with `find` (not via rg --iglob) so behavior is
# identical across ripgrep versions and across macOS bash 3.2 vs Linux
# bash 5.x: apt-installed Ubuntu ripgrep treats bare
# `--iglob 'src/**/*.cs'` (with no positional search path) differently
# than macOS/newer ripgrep, which caused the canary in
# tests/scripts/scrub-gsd-negative.sh to silently pass on CI.

set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

if ! command -v rg >/dev/null 2>&1; then
  echo "::error::ripgrep (rg) not found on PATH. Install via 'brew install ripgrep' locally or 'apt-get install ripgrep' in CI." >&2
  exit 2
fi

readonly PATTERN='phase-[0-9]+|REQ-[A-Z]{2,5}-[0-9]+|\.planning/|PLAN\.md|gsd-|\.gsd/|/get-shit-done/|CONTEXT\.md'

# Paths that are allowed to mention GSD-like tokens. Two kinds of entries:
#   - basename (no slash): exclude any file with that basename anywhere
#     (mirrors the original `rg --iglob '!UPSTREAM.md'` basename semantics).
#   - repo-relative path (contains slash): exclude only that exact path.
EXCLUDE_BASENAMES='^(UPSTREAM\.md|CLAUDE\.md)$'
EXCLUDE_PATHS='^(tools/scrub-gsd\.sh)$'

files=()

collect() {
  local root="$1"; shift
  [[ -d "$root" ]] || return 0
  while IFS= read -r -d '' f; do
    # Strip leading ./ so paths match EXCLUDE_PATHS anchored at repo root.
    f="${f#./}"
    local base="${f##*/}"
    [[ "$base" =~ $EXCLUDE_BASENAMES ]] && continue
    [[ "$f"    =~ $EXCLUDE_PATHS     ]] && continue
    files+=("$f")
  done < <(find "$root" "$@" -type f -print0)
}

# src/: .cs, .md, .yaml, .json, .sh
collect src \( -name '*.cs' -o -name '*.md' -o -name '*.yaml' -o -name '*.json' -o -name '*.sh' \)
# tests/: .cs only
collect tests -name '*.cs'
# docs/: .md only
collect docs -name '*.md'

if (( ${#files[@]} == 0 )); then
  echo "OK: no files in scan surface."
  exit 0
fi

if rg -n -P -e "$PATTERN" -- "${files[@]}"; then
  echo "::error::GSD markers detected. Strip before commit." >&2
  echo "See tools/scrub-gsd.sh for pattern list." >&2
  exit 1
fi

echo "OK: no GSD markers found."
