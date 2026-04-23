#!/usr/bin/env bash
# Generate Python bindings for all six BIFROST proto surfaces.
#
# Invoked by:
#   - Local dev: `cd bifrost-exchange/contracts/python && uv sync && bash build.sh`
#   - CI (contracts-roundtrip slot): same invocation after `uv sync`.
#   - Release workflow (bifrost-exchange/.github/workflows/release.yml): same
#     invocation before `uv build --wheel`.
#
# --pyi_out is non-negotiable per CLAUDE.md §Technology Stack §1.2.
# grpcio-tools ships the bundled protoc; never rely on a system protoc.
#
# Output path contract (LOCKED — Plan 01-07 TYPE_MAP depends on this):
#   After this script runs, every generated file MUST live at
#   ./bifrost_contracts/<surface>/<surface>_pb2.py (and _pb2.pyi, _pb2_grpc.py).
#
# protoc output behaviour (verified empirically with grpcio-tools 1.80):
#   When invoked with `-I ..` and `../<surface>.proto`, grpc_tools.protoc computes
#   the output path from the .proto filename relative to the -I root, NOT from
#   the `package bifrost.<surface>.v1;` declaration. So it emits:
#       bifrost_contracts/<surface>_pb2.py
#       bifrost_contracts/<surface>_pb2.pyi
#       bifrost_contracts/<surface>_pb2_grpc.py
#   directly at the top of --python_out (one level above our target).
#   (The plan's original narrative expected a nested `bifrost/<surface>/v1/`
#   subtree; that path is never produced with this invocation. The post-process
#   below handles the actual observed layout and still guarantees the LOCKED
#   target invariant. The `bifrost_contracts/bifrost/` cleanup + `test ! -d`
#   check remain as belt-and-braces in case a future protoc version ever does
#   honour the package declaration for output paths.)
#
# Unconditional flattening step: move each surface's generated files from the
# top of bifrost_contracts/ DOWN into bifrost_contracts/<surface>/, guaranteeing
# the flat per-surface layout Plan 01-07's TYPE_MAP depends on.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

SURFACES=(market strategy auction round events mc)

# Ensure each surface dir exists (empty __init__.py already committed).
for s in "${SURFACES[@]}"; do
    mkdir -p "bifrost_contracts/${s}"
done

# Step 1: run protoc.
uv run python -m grpc_tools.protoc \
    -I .. \
    --python_out=./bifrost_contracts \
    --grpc_python_out=./bifrost_contracts \
    --pyi_out=./bifrost_contracts \
    ../market.proto \
    ../auction.proto \
    ../round.proto \
    ../events.proto \
    ../strategy.proto \
    ../mc.proto

# Step 2a: unconditionally flatten the observed output shape
# (bifrost_contracts/<surface>_pb2*.py at top of --python_out) INTO
# bifrost_contracts/<surface>/ — the LOCKED target layout for Plan 01-07.
for s in "${SURFACES[@]}"; do
    for ext in _pb2.py _pb2.pyi _pb2_grpc.py; do
        SRC="bifrost_contracts/${s}${ext}"
        if [ -f "$SRC" ]; then
            mv "$SRC" "bifrost_contracts/${s}/${s}${ext}"
        fi
    done
done

# Step 2b: if a future protoc version ever starts honouring the package
# declaration and emits into bifrost_contracts/bifrost/<surface>/v1/, flatten
# that too (defensive; currently a no-op with grpcio-tools 1.78+).
for s in "${SURFACES[@]}"; do
    NESTED="bifrost_contracts/bifrost/${s}/v1"
    if [ -d "$NESTED" ]; then
        # shellcheck disable=SC2086
        mv "$NESTED"/*.py "bifrost_contracts/${s}/" 2>/dev/null || true
        # shellcheck disable=SC2086
        mv "$NESTED"/*.pyi "bifrost_contracts/${s}/" 2>/dev/null || true
    fi
done

# Step 3: delete the empty bifrost_contracts/bifrost/ subtree (if it exists).
if [ -d "bifrost_contracts/bifrost" ]; then
    rm -rf "bifrost_contracts/bifrost"
fi

# Step 4: report what landed where (flat layout check).
echo "Generated (flat layout):"
find bifrost_contracts -maxdepth 3 -name '*_pb2*.py' -o -name '*_pb2*.pyi' | sort

# Step 5: fail fast if the flat-layout invariant is broken.
for s in "${SURFACES[@]}"; do
    if [ ! -f "bifrost_contracts/${s}/${s}_pb2.py" ]; then
        echo "ERROR: expected bifrost_contracts/${s}/${s}_pb2.py not found after flattening" >&2
        exit 1
    fi
done
echo "OK: all six surfaces emit flat bifrost_contracts/<surface>/<surface>_pb2.py"
