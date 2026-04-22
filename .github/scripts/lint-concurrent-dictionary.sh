#!/usr/bin/env bash
# .github/scripts/lint-concurrent-dictionary.sh
# Blocks compound read-modify-write on ConcurrentDictionary on scoring-relevant state.
#
# Forbidden shapes:
#  (a) GetOrAdd(..., factory) where factory is a lambda with a statement body (side-effecting)
#  (b) TryGetValue(key, out var x) followed within 3 lines by mutation on the same dict[key]
#  (c) AddOrUpdate(..., updateValueFactory) where updateValueFactory body has '=' (mutating)
#
# Escape valve: the LINE IMMEDIATELY PRECEDING the violation may contain
#   // bifrost-lint: compound-ok — <reason>
# The reason is captured into the CI log for human review.
#
# Usage: lint-concurrent-dictionary.sh [directory]   (default: src)
# Exit:  0 clean, 1 at least one unescaped violation, 2 usage error.

set -euo pipefail

SCAN_DIR="${1:-src}"

if [ ! -d "$SCAN_DIR" ]; then
    echo "lint-concurrent-dictionary.sh: directory not found: $SCAN_DIR" >&2
    echo "Usage: $0 [directory]" >&2
    exit 2
fi

# HARD PRECONDITION: ripgrep must be a real executable on PATH.
# Some interactive shells define an `rg` function that shadows the binary (notably
# the Claude Code wrapper, which exits 0 with no output when invoked in a subshell
# without the parent process). That would turn this lint into a silent no-op — the
# exact failure mode the LintFenceFixtures exist to prevent.
if ! command -v rg >/dev/null 2>&1 || ! rg --version >/dev/null 2>&1; then
    echo "lint-concurrent-dictionary.sh: ripgrep (rg) is not available or not executable." >&2
    echo "Install it: apt-get install -y ripgrep  |  brew install ripgrep" >&2
    echo "If rg IS installed but this message fires, your shell may be shadowing it with a function;" >&2
    echo "re-run with \`env -i PATH=\$PATH bash $0 $SCAN_DIR\` to bypass shell rcs." >&2
    exit 2
fi

EXIT=0
# Use absolute/real rg via `command` to bypass any shell function shadow.
RG="command rg --color=never --no-heading -n"

# Reports a violation unless the line immediately preceding the hit carries
# the escape-valve comment. Called per ripgrep hit.
check_hit() {
    local shape_label="$1"      # "a", "b", or "c"
    local hint="$2"             # remediation hint
    local file="$3"
    local line="$4"
    local prev=$((line - 1))
    if [ "$prev" -ge 1 ] && sed -n "${prev}p" "$file" 2>/dev/null | grep -q 'bifrost-lint: compound-ok'; then
        echo "::notice file=$file,line=$line::shape-($shape_label) escape-valve honored: $(sed -n "${prev}p" "$file" | sed 's/^[[:space:]]*//')"
    else
        echo "::error file=$file,line=$line::ConcurrentDictionary compound-op shape ($shape_label) — $hint"
        EXIT=1
    fi
}

# Shape (a): GetOrAdd with a statement-bodied lambda (matches `=>` followed by `{`).
# Expression-bodied lambdas (`=> expr`) are exempt.
while IFS= read -r hit; do
    [ -z "$hit" ] && continue
    file=$(echo "$hit" | cut -d: -f1)
    line=$(echo "$hit" | cut -d: -f2)
    check_hit "a" \
        "GetOrAdd with statement-bodied factory; use expression-body, Monitor lock, or add '// bifrost-lint: compound-ok — <reason>'." \
        "$file" "$line"
done < <($RG '\.GetOrAdd\s*\([^,]+,\s*(\w+\s*=>|\([^)]*\)\s*=>)\s*\{' "$SCAN_DIR" --type cs || true)

# Shape (b): TryGetValue followed by an indexer-assignment within 3 lines.
# CI's apt ripgrep on ubuntu-latest is built WITHOUT PCRE2, so backreferences are
# unavailable. We approximate shape (b) without them by flagging any TryGetValue
# that has ANY dict[...] = ... / += / -= / ++ / -- statement in the next 3 lines.
# This may over-match when two different dicts are used in the same neighborhood;
# the escape-valve comment is the author's opt-out for intentional patterns.
while IFS= read -r hit; do
    [ -z "$hit" ] && continue
    file=$(echo "$hit" | cut -d: -f1)
    line=$(echo "$hit" | cut -d: -f2)
    # Extract the 3 lines following the TryGetValue call and check for an indexer-assignment.
    next_end=$((line + 3))
    window=$(sed -n "$((line+1)),${next_end}p" "$file" 2>/dev/null || true)
    if echo "$window" | $RG -q '\w+\s*\[[^\]]+\]\s*(=[^=]|\+\+|--|\+=|-=)' -; then
        check_hit "b" \
            "TryGetValue followed by indexer-assignment within 3 lines; wrap in Monitor lock or add '// bifrost-lint: compound-ok — <reason>'." \
            "$file" "$line"
    fi
done < <($RG -n '\.TryGetValue\s*\(' "$SCAN_DIR" --type cs || true)

# Shape (c): AddOrUpdate with a mutating updateValueFactory body.
# Heuristic: the updater body (between `=>` and `}`) contains `=` that is NOT `==`, `!=`, `<=`, `>=`.
while IFS= read -r hit; do
    [ -z "$hit" ] && continue
    file=$(echo "$hit" | cut -d: -f1)
    line=$(echo "$hit" | cut -d: -f2)
    check_hit "c" \
        "AddOrUpdate with mutating updater; replace with immutable rebuild or Monitor lock, or add '// bifrost-lint: compound-ok — <reason>'." \
        "$file" "$line"
done < <($RG -U --multiline-dotall '\.AddOrUpdate\s*\([^)]+,\s*\([^)]+\)\s*=>\s*\{[^}]*[^=!<>]=[^=]' "$SCAN_DIR" --type cs || true)

if [ "$EXIT" -ne 0 ]; then
    echo "::error::lint-concurrent-dictionary.sh found one or more violations — see the ::error:: lines above."
else
    echo "lint-concurrent-dictionary.sh: clean ($SCAN_DIR)"
fi

exit $EXIT
