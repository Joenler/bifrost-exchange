# bifrost-exchange

Central-machine monorepo for **BIFROST** — the LAN intraday-power-trading hackathon.
Runs the exchange, quoter NPC, imbalance simulator, DAH auction, round orchestrator,
gateway, recorder, MC console, and spectator big-screen from a single
`docker compose up`.

> This repo is the central-machine authority. Team-facing strategy frameworks live in
> the sibling [`bifrost-algo`](../bifrost-algo) repo.

## Quick start

```bash
git clone https://github.com/Joenler/bifrost-exchange.git
cd bifrost-exchange
docker compose up --wait
```

`docker compose up --wait` boots RabbitMQ + all central-machine services with
HEALTHCHECK coverage; the command exits 0 once every container reports `healthy`
(~30s on commodity hardware). `docker compose down` tears everything down cleanly.

## Top-level layout

| Path                                | What lives here                                                                                     |
|-------------------------------------|-----------------------------------------------------------------------------------------------------|
| `contracts/`                        | gRPC protobuf wire contracts — published as NuGet + pip from CI.                                    |
| `src/contracts-internal/`           | Internal RabbitMQ DTOs, forked from Arena's `Contracts/`.                                           |
| `src/<service>/` (one per service)  | `exchange`, `quoter`, `imbalance`, `dah-auction`, `orchestrator`, `gateway`, `recorder`, `mc-console`. Thin Worker skeletons today; real logic lands in later work. |
| `bigscreen/`                        | Vite + React 19 + TS 5.7 spectator dashboard. "Hello BIFROST" scaffold today; live data later.      |
| `tools/mc/`                         | MC-console helpers (reserved path).                                                                 |
| `tests/`                            | xUnit test projects per service (`Bifrost.<Service>.Tests`) + lint-fence fixtures.                  |
| `docs/gateway-mapping.md`           | gRPC ↔ RabbitMQ DTO translation map.                                                                |
| `docker-compose.yml`                | Central-machine composition: RabbitMQ + every service container.                                    |
| `UPSTREAM.md`                       | Root provenance note; every Arena donation carries a folder-level `UPSTREAM.md`.                    |
| `LICENSE`                           | MIT.                                                                                                |

## Engineering invariants

- **.NET**: `net10.0`, nullable enabled, `TreatWarningsAsErrors`, implicit usings, Allman braces, 4-space indent, xUnit v3.
- **Determinism**: never `System.Random.Shared`; never un-injected `System.DateTime.UtcNow`. Use an injected `Bifrost.Time.IClock` (see `src/common/Bifrost.Time/`). Enforced by the `ci-lint` CI job via `Microsoft.CodeAnalysis.BannedApiAnalyzers` + `build/BannedSymbols.txt`.
- **Concurrency**: the matching engine is single-writer; no locks. Per-team state in the gateway uses `Monitor` locks with documented ordering. No `ConcurrentDictionary<,>` compound operations on scoring-relevant state — enforced by the `ci-lint` ripgrep step at `.github/scripts/lint-concurrent-dictionary.sh`.
- **Copy-with-provenance**: every file donated from Arena carries a folder-level `UPSTREAM.md` with the source commit SHA. No internal planning markers leak into source.

See [`CLAUDE.md`](./CLAUDE.md) for the full engineering-rigor rules.

## Architecture decisions

The BIFROST program keeps its architecture decision records in the sibling
`bifrost-program/` planning hub (organizer-internal; kept local and not published
to GitHub). The links below resolve when this repo and `bifrost-program/` are
cloned as siblings under the same parent directory — which is the documented
organizer workstation setup.

- [`ADR-0002` — gRPC Strategy Gateway](../bifrost-program/ADRs/ADR-0002-grpc-strategy-gateway.md) — team-facing wire decision.
- [`ADR-0003` — Aggregate-position imbalance pricing](../bifrost-program/ADRs/ADR-0003-imbalance-pricing-model.md).
- [`ADR-0004` — Fair-play guards](../bifrost-program/ADRs/ADR-0004-fair-play-guards.md).
- [`ADR-0005` — Command-driven MC console](../bifrost-program/ADRs/ADR-0005-mc-console-command-driven.md).
- [`ADR-0006` — Two-code-repo layout](../bifrost-program/ADRs/ADR-0006-two-code-repo-layout.md) — supersedes ADR-0001.

## License

MIT — see [`LICENSE`](./LICENSE).
