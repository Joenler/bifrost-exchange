# bifrost_contracts — Python wheel build

Python bindings for the six `.proto` files under `bifrost-exchange/contracts/`.

## Local development

```bash
cd bifrost-exchange/contracts/python
uv sync
bash build.sh
uv build --wheel
# → dist/bifrost_contracts-0.0.0-py3-none-any.whl
```

## Layout (post build.sh)

```
python/
  pyproject.toml
  build.sh
  bifrost_contracts/
    __init__.py
    market/
      __init__.py
      # market_pb2.py, market_pb2.pyi (generated; .gitignored)
    strategy/  ...  (same pattern)
    auction/   ...
    round/     ...
    events/    ...
    mc/        ...
```

`build.sh` UNCONDITIONALLY flattens protoc's package-name-driven output path
(`bifrost_contracts/bifrost/<surface>/v1/…`) into the flat layout shown above.
Plan 01-07's polyglot round-trip harness assumes this exact path shape.

Import shape:

```python
from bifrost_contracts.strategy import strategy_pb2, strategy_pb2_grpc
from bifrost_contracts.market import market_pb2
```

## Do not

- Do not commit `*_pb2.py`, `*_pb2.pyi`, `*_pb2_grpc.py` — they are regenerated
  from `../*.proto` every build (Phase 00 `.gitignore` enforces).
- Do not depend on Pydantic at the wire layer (banned per ADR-0002; use the
  generated protobuf classes directly).
- Do not use `buf generate` for codegen — we use `grpcio-tools` only. See
  `../README.md` for the rationale.
