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
