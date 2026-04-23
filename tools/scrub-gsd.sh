#!/usr/bin/env bash
# tools/scrub-gsd.sh — single source of truth for GSD-marker scrubbing.
# Invoked by: .git/hooks/pre-commit (via tools/install-hooks.sh)
#             .github/workflows/ci.yml (ci-scrub-gsd job)
# Exits non-zero on any match.

set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

if ! command -v rg >/dev/null 2>&1; then
  echo "::error::ripgrep (rg) not found on PATH. Install via 'brew install ripgrep' locally or 'apt-get install ripgrep' in CI." >&2
  exit 2
fi

readonly PATTERN='phase-[0-9]+|REQ-[A-Z]{2,5}-[0-9]+|\.planning/|PLAN\.md|gsd-|\.gsd/|/get-shit-done/|CONTEXT\.md'

readonly INCLUDES=(
  'src/**/*.cs'
  'src/**/*.md'
  'src/**/*.yaml'
  'src/**/*.json'
  'src/**/*.sh'
  'tests/**/*.cs'
  'docs/**/*.md'
)
readonly EXCLUDES=(
  '!UPSTREAM.md'
  '!tools/scrub-gsd.sh'
  '!CLAUDE.md'
)

rg_args=(-n -P -e "$PATTERN")
for inc in "${INCLUDES[@]}"; do rg_args+=(--iglob "$inc"); done
for exc in "${EXCLUDES[@]}"; do rg_args+=(--iglob "$exc"); done

if rg "${rg_args[@]}"; then
  echo "::error::GSD markers detected. Strip before commit." >&2
  echo "See tools/scrub-gsd.sh for pattern list." >&2
  exit 1
fi

echo "OK: no GSD markers found."
