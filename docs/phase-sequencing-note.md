# Contracts-internal scope: Phase 01 vs Phase 02 split

(Authored during the exchange fork & simplification phase, 2026-04-23.)

## What Phase 01 owned

- The six `.proto` files under `bifrost-exchange/contracts/`
- The C# NuGet + Python pip packages published from `contracts/` on tag push
- The translation-roundtrip CI job between proto types and the internal DTOs in
  `src/contracts-internal/Bifrost.Contracts.Internal/`

## What Phase 02 owns

- `src/contracts-internal/Bifrost.Contracts.Internal/` — internal DTO fork consumed by
  `Exchange.Infrastructure.RabbitMq` + `Recorder.Infrastructure` (already landed in
  Phase 01 as commit `b296bfa`; Phase 02 performs the audit + provenance refresh, not a
  re-fork)
- Donated Arena `Exchange.Domain`, `Exchange.Application`, `Exchange.Infrastructure.RabbitMq`,
  `Exchange.Api` (as the exchange host `Program.cs`)
- Donated Arena `ArenaRecorder/{Storage,Session,Infrastructure}` plus a new
  `src/recorder/Migrations/001_initial.sql` + `SchemaMigrator`

## Why this split

The DTO fork was bundled into Phase 01 as a prerequisite so the exchange donation would
compile without a stub layer. The present phase therefore owns the audit + any pruning, and
authored this note to make the split visible to downstream phases.

## Arena commit SHA in use

Every donated folder under `src/exchange/` and `src/recorder/` carries an `UPSTREAM.md`
file recording the authoritative Arena commit SHA `5f8da6072978a4693de7c7ec7f5ff9ea22181a0b`.

## PhysicalShockCmd quarter_index — required-flag enforcement

Added in v1.1.0 (imbalance-simulator wire): `optional int32 quarter_index = 4;` on both
`mc.proto::PhysicalShockCmd` and `events.proto::PhysicalShock`. Because proto3 optional
fields default to 0 when unset, a consumer that reads `shock.QuarterIndex` without
checking `HasQuarterIndex` would silently bias all shocks to Q1 — a silent settlement
corruption.

The imbalance simulator handles this at its boundary with a
`Debug.Assert(msg.HasQuarterIndex)` in `ShockConsumerHostedService` plus a release-mode
guarded log + discard — but that is defense-in-depth.

The authoritative boundary enforcement belongs earlier in the pipeline:

- The future MC console CLI (Phase 06b) MUST make `--quarter` a required argparse
  argument on the `physical-shock` subcommand. Invocation without `--quarter` must exit
  non-zero BEFORE any gRPC send.
- The future round orchestrator (Phase 06) MUST validate `HasQuarterIndex` on the
  received `PhysicalShockCmd` in its MC gRPC handler. Missing field returns a typed
  `McCommandResult { success: false, message: "quarter_index required" }` and does NOT
  forward to the simulator.

The imbalance simulator trusts that Phase 06 and Phase 06b honor this invariant.
Simulator assertions exist as regression catchers, not as the primary contract.

## Phase 05 dah-auction closure

Phase 05 stood up `bifrost-exchange/src/dah-auction/` as an ASP.NET Core 10
Minimal API service accepting `POST /auction/bid` on container + host port
8080. Single-writer actor loop via `Channel<IAuctionCommand>` mutates the
in-memory `(team_name, quarter_id) -> BidMatrix` map; `IRoundStateSource`
drives `AuctionOpen -> AuctionClosed` clearing via the
`UniformPriceClearing.Compute` algorithm (pedagogically documented per the
EUPHEMIA Public Description).

Wire topology added in Phase 05:

- NEW direct exchange `bifrost.auction` for `ClearingResult` payload fan-out
  (Phase 07 Gateway consumes per-team).
- REUSE existing `bifrost.public` events exchange with routing keys
  `events.auction.bid` / `events.auction.cleared` / `events.auction.no_cross`
  (recorder audit trail).

Recorder binding closure: the recorder queue now additionally binds to
`bifrost.public` on pattern `events.#`. This catches the pre-existing
quoter `events.regime.change` emissions plus all Phase 05 auction audit
events. The legacy `bifrost.events.v1` exchange bindings (`order.#`,
`lifecycle.#`) are preserved for compatibility with potential
reintroduction of direct order/lifecycle publishers.

Host port 8080 is bound by `dah-auction` on the compose stack. Phase 07
Gateway and any future HTTP-facing services must negotiate alternative
ports.
