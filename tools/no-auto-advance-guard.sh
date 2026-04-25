#!/usr/bin/env bash
# tools/no-auto-advance-guard.sh — falsifiable regression guard.
#
# Phase 06 ships zero timer-driven state-transition code. The only legitimate
# `PeriodicTimer` / `Timer foo` / `Task.Delay(` occurrences in
# `src/orchestrator/**/*.cs` are in three explicitly-whitelisted files:
#
#   1. src/orchestrator/Actor/IterationSeedTimer.cs
#      Iteration-seed rotation timer. Wakes every real second to gate a logical
#      tick on IClock; the actor's tick handler only mutates the iteration seed
#      counter — it NEVER changes RoundStateMachine.Current.
#
#   2. src/orchestrator/Heartbeat/HeartbeatToleranceMonitor.cs
#      Heartbeat-tolerance polling BackgroundService. Sets the Blocked flag on
#      heartbeat-loss; never changes RoundStateMachine.Current.
#
#   3. src/orchestrator/StartupLogger.cs
#      Phase 00 sentinel-file BackgroundService. The single
#      `Task.Delay(Timeout.Infinite, stoppingToken)` is a graceful idle pattern
#      with no transition semantics — the host's IHostApplicationLifetime
#      cancels stoppingToken on SIGTERM and propagation handles shutdown.
#
# Any OTHER file in src/orchestrator/**/*.cs containing the timer pattern is a
# regression: introduce one and this guard fails. Any `AutoAdvance` symbol or
# `"auto-advance"` string literal anywhere in src/orchestrator/** or in
# appsettings.json also fails this guard.
#
# Invoked by: developer machine pre-commit (manual) and CI (Plan 06-14 will
# register this script in .github/workflows/ci.yml as the ci-no-auto-advance
# matrix slot). Exits 0 on success, 1 on any violation, 2 on missing tooling.

set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

# -----------------------------------------------------------------------------
# Tooling check (matches scrub-gsd.sh convention).
# -----------------------------------------------------------------------------
if ! command -v rg >/dev/null 2>&1; then
    echo "::error::ripgrep (rg) not found on PATH. Install via 'brew install ripgrep' locally or 'apt-get install ripgrep' in CI." >&2
    exit 2
fi

ORCH_DIR="src/orchestrator"
APPSETTINGS="$ORCH_DIR/appsettings.json"
TEST_DIR="tests"

if [[ ! -d "$ORCH_DIR" ]]; then
    echo "::error::no-auto-advance-guard.sh: $ORCH_DIR not found from $(pwd)" >&2
    exit 2
fi

# -----------------------------------------------------------------------------
# fail() prints a structured failure block with SPEC + ADR provenance and exits.
# -----------------------------------------------------------------------------
fail() {
    echo ""
    echo "========================================"
    echo "FAIL: no-auto-advance-guard.sh"
    echo "========================================"
    printf '%b\n' "$@"
    echo ""
    echo "Phase 06 ships zero timer-driven state-transition code; every state"
    echo "transition requires an explicit MC command. See ADR-0005 Amendments"
    echo "(2026-04-24) for the override decision and the orchestrator phase"
    echo "specification for the falsifiable requirement."
    exit 1
}

# -----------------------------------------------------------------------------
# Guard (b): timer patterns in src/orchestrator/**/*.cs
#
# Pattern matches:
#   - `PeriodicTimer` (System.Threading.PeriodicTimer)
#   - `Timer <ident>` (typed Timer field/parameter — won't match the type-name
#                     occurrence `class IterationSeedTimer` because
#                     `IterationSeedTimer` is a single word; won't match prose
#                     because `[A-Za-z_]` requires an identifier-character
#                     follow-up, ruling out punctuation/end-of-line)
#   - `Task.Delay(`
#
# Whitelist: three files allowed to contain these patterns.
# -----------------------------------------------------------------------------
TIMER_REGEX='(\bPeriodicTimer\b|\bTimer\s+[A-Za-z_]|Task\.Delay\()'

# Whitelist by basename (file lives in any subdirectory of src/orchestrator/).
whitelist=(
    "IterationSeedTimer.cs"
    "HeartbeatToleranceMonitor.cs"
    "StartupLogger.cs"
)

is_whitelisted() {
    local base="$1"
    for w in "${whitelist[@]}"; do
        if [[ "$base" == "$w" ]]; then
            return 0
        fi
    done
    return 1
}

echo "Guard (b): scanning $ORCH_DIR for timer patterns..."

# rg -l prints file paths with at least one match. We then partition into
# whitelisted / unauthorized buckets; unauthorized hits fail.
unauthorized_files=""
matched_files=""
while IFS= read -r f; do
    [[ -z "$f" ]] && continue
    matched_files="${matched_files}${f}\n"
    base="${f##*/}"
    if ! is_whitelisted "$base"; then
        unauthorized_files="${unauthorized_files}  $f\n"
    fi
done < <(rg -l --type cs -e "$TIMER_REGEX" "$ORCH_DIR" 2>/dev/null || true)

if [[ -n "$unauthorized_files" ]]; then
    fail "Unauthorized timer pattern(s) in $ORCH_DIR outside the whitelist:\n$unauthorized_files\nWhitelist (files permitted to contain timer patterns):\n  ${whitelist[*]}\n\nIf you intentionally added a non-transition timer, add the basename to\nthe whitelist array in tools/no-auto-advance-guard.sh and document the\nrationale in the file's header comment.\n\nIf the new timer drives a state transition, this is a SPEC Req 13 violation\nand must be reverted."
fi

# Sanity: each whitelisted file MUST exist and MUST contain a timer pattern.
# Catches a silent regression where someone removes a whitelisted file's timer
# code path but forgets to remove it from the whitelist (which would then
# silently mask a future regression in a different file).
for w in "${whitelist[@]}"; do
    found=$(find "$ORCH_DIR" -name "$w" -type f 2>/dev/null | head -1 || true)
    if [[ -z "$found" ]]; then
        fail "Whitelisted file $w not found anywhere under $ORCH_DIR.\nThe whitelist is stale — remove the entry or restore the file."
    fi
    if ! rg -q --type cs -e "$TIMER_REGEX" "$found"; then
        fail "Whitelisted file $w (at $found) was expected to contain a timer\npattern but does not. The whitelist is stale — remove the entry from\ntools/no-auto-advance-guard.sh."
    fi
done

echo "  PASS — timer patterns confined to ${#whitelist[@]} whitelisted files."

# -----------------------------------------------------------------------------
# Guard (c): AutoAdvance symbol in src/orchestrator/** and appsettings.json
# -----------------------------------------------------------------------------
echo "Guard (c): scanning for AutoAdvance symbol..."
auto_advance_hits=$(rg -n --type-add 'cfg:*.json' --type cs --type cfg "AutoAdvance" "$ORCH_DIR" 2>/dev/null || true)
if [[ -n "$auto_advance_hits" ]]; then
    fail "AutoAdvance symbol found in $ORCH_DIR:\n$auto_advance_hits\n\nSPEC Req 13 forbids any AutoAdvance class / property / config key. Remove\nthe symbol or rename it to something that does not imply timer-driven\ntransitions."
fi
if [[ -f "$APPSETTINGS" ]] && rg -q "AutoAdvance" "$APPSETTINGS" 2>/dev/null; then
    fail "AutoAdvance symbol found in $APPSETTINGS:\n$(rg -n 'AutoAdvance' "$APPSETTINGS")\n\nSPEC Req 13 forbids any AutoAdvance config key."
fi
echo "  PASS — no AutoAdvance symbol."

# -----------------------------------------------------------------------------
# Guard (d): "auto-advance" string literal in orchestrator source + tests
#
# Matches the literal six-character string `"auto-advance"` (with surrounding
# double-quotes). Prose mentions in comments (e.g. "no auto-advance") do NOT
# match because the regex requires the surrounding quotes; this avoids false
# positives in module-header XML doc comments while still catching any string
# literal embedded in code (e.g. operator_host="auto-advance" audit row tags).
# -----------------------------------------------------------------------------
echo "Guard (d): scanning for \"auto-advance\" string literal..."
if rg -q '"auto-advance"' "$ORCH_DIR" 2>/dev/null; then
    fail "\"auto-advance\" string literal found in $ORCH_DIR:\n$(rg -n '"auto-advance"' "$ORCH_DIR")"
fi
if [[ -d "$TEST_DIR" ]] && rg -q '"auto-advance"' "$TEST_DIR" 2>/dev/null; then
    fail "\"auto-advance\" string literal found in $TEST_DIR:\n$(rg -n '"auto-advance"' "$TEST_DIR")\n\nA test fixture should not embed the auto-advance string — if the test\nintends to exercise the no-auto-advance regression guard, drive the actor\ndirectly through MC commands instead of constructing audit rows with the\nforbidden tag."
fi
echo "  PASS — no \"auto-advance\" string literal."

# -----------------------------------------------------------------------------
# All guards passed.
# -----------------------------------------------------------------------------
echo ""
echo "========================================"
echo "PASS: no-auto-advance-guard.sh"
echo "========================================"
echo "Phase 06 orchestrator is auto-advance-free."
exit 0
